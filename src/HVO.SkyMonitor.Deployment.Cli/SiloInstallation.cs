using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The installer's Silo-layout steps (#1966; CameraAgent Silo cutover contract 2, 4.3, 7.2 and 8.1): the instance's own
/// credentials, the bootstrap of an empty versioned bucket with its create-only marker and generation-1 fence before
/// CameraAgent first starts, and the read-only verification an install, a first resume and <c>silo-verify</c> share.
/// </summary>
internal static class SiloInstallation
{
    private const string RootUserFile = "root-user";
    private const string RootPasswordFile = "root-password";
    private const string FirstBootIdFile = "first-boot-id";
    private const string MarkerFile = "instance.v1.json";
    private const string FenceFile = "fence.json";
    private const string IntentFile = "intent.json";
    private const string CredentialFile = "writer-credential.json";
    private const string IntentFormat = "hvo.cameraagent.silo.bootstrap-intent/1";
    private const string CredentialFormat = "hvo.cameraagent.silo.writer-credential/1";

    private static readonly TimeSpan HealthBudget = TimeSpan.FromMinutes(2);

    /// <summary>Creates the Silo root credential once; it is mounted read-only into the Silo container and nowhere else.</summary>
    public static void EnsureRootCredential(string directory)
    {
        SafeFileSystem.CreateOwnerDirectory(directory);
        foreach (var (name, bytes) in new[] { (RootUserFile, 12), (RootPasswordFile, 32) })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                SafeFileSystem.ValidateOwnerFile(path);
                continue;
            }
            SafeFileSystem.WriteTextAtomic(path, "hvo" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes)));
        }
    }

    /// <summary>The instance's first-boot pause identity, created once and kept so a resumed install seeds the same one.</summary>
    public static Guid GetOrCreateFirstBootPauseId(InstallationPaths paths, bool allowCreation)
    {
        var directory = SiloRuntimeProfile.BootstrapDirectory(paths);
        var path = Path.Combine(directory, FirstBootIdFile);
        if (File.Exists(path))
        {
            SafeFileSystem.ValidateOwnerFile(path);
            return Guid.TryParseExact(File.ReadAllText(path).Trim(), "D", out var existing) && existing != Guid.Empty
                ? existing
                : throw new InstallerException("The retained first-boot pause identity is invalid.");
        }
        if (!allowCreation) return Guid.Empty;
        SafeFileSystem.CreateOwnerDirectory(directory);
        var created = Guid.NewGuid();
        SafeFileSystem.WriteTextAtomic(path, created.ToString("D"));
        return created;
    }

    /// <summary>
    /// Starts only the Silo service, proves its data root belongs to it alone, and bootstraps the bucket. Re-entrant: a
    /// resumed install reuses the saved writer credential and the exact marker and fence bytes it wrote before, and a
    /// store holding anything this instance did not create is refused.
    /// </summary>
    public static async Task<SiloInstallationIdentity> BootstrapAsync(
        DockerClient docker,
        ComposeFiles compose,
        InstallationPaths paths,
        Guid instanceId,
        Guid firstBootPauseId,
        SiloImageIdentity image,
        (string FileSystem, string Device) dataRoot,
        CancellationToken cancellationToken)
    {
        var container = SiloRuntimeProfile.ContainerNameFor(instanceId);
        var dataRootPath = SiloRuntimeProfile.DataRoot(paths);
        var bucket = SiloRuntimeProfile.BucketFor(instanceId);
        var secrets = Path.Combine(paths.ConfigRoot, "secrets");
        var accessPath = Path.Combine(secrets, SiloRuntimeProfile.WriterAccessKeySetting);
        var secretPath = Path.Combine(secrets, SiloRuntimeProfile.WriterSecretKeySetting);
        var bootstrap = SiloRuntimeProfile.BootstrapDirectory(paths);
        SafeFileSystem.CreateOwnerDirectory(bootstrap);
        var intentPath = Path.Combine(bootstrap, IntentFile);
        var credentialPath = Path.Combine(bootstrap, CredentialFile);

        // Every retained bootstrap record is checked before the store is touched: an intent must be this instance's own
        // for this data root, a credential record this instance's own, and a writer setting file already written must
        // equal the record it came from. Anything else is ambiguous and refused before any store call.
        var expectedIntent = BootstrapIntent(instanceId, dataRootPath, dataRoot.Device);
        var intent = ReadRecord(intentPath);
        if (intent is not null && !System.Text.Json.Nodes.JsonNode.DeepEquals(intent, expectedIntent))
            throw InstallerException.Refused(
                "The retained Silo bootstrap intent is not this instance's for this data root. Nothing was changed in the store.");
        var credential = ReadCredentialRecord(credentialPath, instanceId, bucket);
        var accessWritten = File.Exists(accessPath);
        var secretWritten = File.Exists(secretPath);
        if (credential is null && (accessWritten || secretWritten))
        {
            if (!(accessWritten && secretWritten) || intent is not null)
                throw InstallerException.Refused(
                    "The Silo writer credential is only partly written and has no record to complete it from. Nothing was changed in the store.");
            // Both setting files of an install that predates the record: they are the credential, so they become its record.
            var (legacyAccess, legacySecret) = ReadWriterCredential(paths);
            credential = (legacyAccess, legacySecret);
            SafeFileSystem.WriteTextAtomic(credentialPath, CredentialRecord(instanceId, bucket, legacyAccess, legacySecret));
        }
        if (credential is { } recorded)
        {
            foreach (var (path, value) in new[] { (accessPath, recorded.Access), (secretPath, recorded.Secret) })
            {
                if (File.Exists(path) &&
                    !string.Equals(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), value, StringComparison.Ordinal))
                    throw InstallerException.Refused(
                        "A Silo writer setting file differs from the recorded writer credential. Nothing was changed in the store.");
            }
        }

        await docker.EnsureSiloDataRootExclusiveAsync(dataRootPath, container, cancellationToken).ConfigureAwait(false);
        await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName,
            ["up", "--detach", "--no-deps", SiloRuntimeProfile.ServiceName], cancellationToken).ConfigureAwait(false);
        await docker.WaitForSiloHealthyAsync(container, HealthBudget, cancellationToken).ConfigureAwait(false);
        _ = await VerifyContainerAsync(docker, instanceId, container, dataRootPath, image, expectApplication: false, cancellationToken)
            .ConfigureAwait(false);

        if (credential is null)
        {
            // The intent is durable before the store changes, so a run that loses the acknowledgement of the bucket or of
            // the writer key resumes by adopting exactly what this instance's own attempt may have created.
            Task<ProcessResult> Execute(string script) => docker.ExecSiloScriptAsync(container, script, cancellationToken);
            (string AccessKey, string SecretKey) created;
            if (intent is null)
            {
                // A fresh bootstrap first proves, read-only, that the store is empty. Only then is the intent recorded, so
                // a store this run did not find empty can never become one it later adopts; the creation itself acts only
                // if the store is still exactly that empty inventory.
                var empty = await SiloBootstrapStore.ProveEmptyAsync(Execute, bucket, cancellationToken).ConfigureAwait(false);
                SafeFileSystem.WriteTextAtomic(intentPath, expectedIntent.ToJsonString());
                created = await SiloBootstrapStore.CreateAsync(Execute, bucket, SiloRuntimeProfile.WriterGeneration, empty, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var rootUserPath = Path.Combine(SiloRuntimeProfile.RootSecretsDirectory(paths), RootUserFile);
                SafeFileSystem.ValidateOwnerFile(rootUserPath);
                var rootUser = (await File.ReadAllTextAsync(rootUserPath, cancellationToken).ConfigureAwait(false)).Trim();
                created = await SiloBootstrapStore.AdoptAsync(Execute, bucket, SiloRuntimeProfile.WriterGeneration, rootUser, cancellationToken)
                    .ConfigureAwait(false);
            }
            var (createdAccess, createdSecret) = created;
            if (!SiloRuntimeProfile.AccessKeyPattern().IsMatch(createdAccess) || !SiloRuntimeProfile.SecretKeyPattern().IsMatch(createdSecret))
                throw new InstallerException("The Silo writer credential is not in the expected form.");
            // One durable record holds both halves before either setting file is written, so an interruption between the
            // two setting files resumes by writing them again from it.
            SafeFileSystem.WriteTextAtomic(credentialPath, CredentialRecord(instanceId, bucket, createdAccess, createdSecret));
            credential = (createdAccess, createdSecret);
        }
        var (access, secret) = credential.Value;
        if (!File.Exists(secretPath)) SafeFileSystem.WriteTextAtomic(secretPath, secret);
        if (!File.Exists(accessPath)) SafeFileSystem.WriteTextAtomic(accessPath, access);
        if (ReadWriterCredential(paths) != (access, secret))
            throw new InstallerException("The Silo writer setting files do not hold the recorded credential.");

        var now = DateTimeOffset.UtcNow;
        var marker = FixedBytes(Path.Combine(bootstrap, MarkerFile), () => SiloRuntimeProfile.Marker(instanceId, now));
        var fence = FixedBytes(Path.Combine(bootstrap, FenceFile), () => SiloRuntimeProfile.Fence(instanceId, access, now));
        if (!SiloRuntimeProfile.MarkerBinds(marker, instanceId) || !FenceNames(fence, access))
            throw new InstallerException("The retained Silo bootstrap documents do not belong to this instance and writer.");
        var bound = SiloRuntimeProfile.ParseScriptResult(
            await docker.ExecSiloScriptAsync(container,
                SiloRuntimeProfile.WriteBindingScript(bucket, access, secret, marker, fence), cancellationToken).ConfigureAwait(false),
            "bound",
            "binding");
        var markerSha = Convert.ToHexStringLower(SHA256.HashData(marker));
        var fenceSha = Convert.ToHexStringLower(SHA256.HashData(fence));
        if (bound.GetProperty("markerSha256").GetString() != markerSha || bound.GetProperty("fenceSha256").GetString() != fenceSha)
            throw new InstallerException("The Silo binding documents did not read back with their exact bytes.");
        var storeId = SiloRuntimeProfile.StoreIdFor(instanceId);
        return new SiloInstallationIdentity(
            SiloRuntimeProfile.StateContract,
            image.Reference,
            image.Release,
            SiloRuntimeProfile.IndexDigest,
            image.PlatformDigest,
            image.ImageId,
            image.Revision,
            dataRootPath,
            dataRoot.FileSystem,
            dataRoot.Device,
            storeId,
            bucket,
            SiloRuntimeProfile.StorageBindingIdFor(instanceId, storeId, bucket),
            SiloRuntimeProfile.WriterGeneration,
            access,
            bound.GetProperty("markerVersionId").GetString()!,
            markerSha,
            bound.GetProperty("fenceVersionId").GetString()!,
            fenceSha,
            firstBootPauseId,
            DiskSyncOnWrite: true);
    }

    /// <summary>
    /// The read-only Silo checks (contract 2, 4.2, 8.1). Refused when the runtime is not the installed one: the container
    /// is not the pinned image with no published port, only the internal network, a read-only root, the console off and
    /// disk sync on; the data root is not the recorded one on the recorded filesystem or another container mounts it; the
    /// bucket is not versioned; the marker is not the recorded exact version binding this instance; or the store's current
    /// fence is not a same-generation fence of this binding naming the writer key CameraAgent holds. A wrong endpoint,
    /// bucket, store or credential fails here. Headroom and the optional archive are reported, not thrown, so the caller
    /// decides readiness.
    /// </summary>
    public static async Task<SiloVerification> VerifyAsync(
        DockerClient docker,
        InstallationPaths paths,
        Guid instanceId,
        SiloInstallationIdentity silo,
        ImageInstallationIdentity application,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        var container = SiloRuntimeProfile.ContainerNameFor(instanceId);
        var expectedRoot = SiloRuntimeProfile.DataRoot(paths);
        if (silo.DataRoot != expectedRoot || silo.Bucket != SiloRuntimeProfile.BucketFor(instanceId) ||
            silo.StoreId != SiloRuntimeProfile.StoreIdFor(instanceId) ||
            silo.StorageBindingId != SiloRuntimeProfile.StorageBindingIdFor(instanceId, silo.StoreId, silo.Bucket))
            throw InstallerException.Refused("The recorded Silo binding does not belong to this instance.");
        // The bucket and binding CameraAgent is configured with must be the recorded ones, before Docker is asked anything:
        // the probe below reaches the recorded bucket, so a stale or foreign setting would otherwise pass unseen.
        EnsureConfiguredBinding(paths, silo);
        var (fileSystem, _, device) = SiloRuntimeProfile.MountOf(
            expectedRoot, await File.ReadAllTextAsync(HostProductRootPolicy.MountInfoPath, cancellationToken).ConfigureAwait(false));
        if (fileSystem != silo.DataRootFileSystem || device != silo.DataRootDevice)
            throw InstallerException.Refused("The Silo data root is no longer on the filesystem it was installed on.");
        var platform = SiloImagePin.Platforms.FirstOrDefault(candidate => candidate.ManifestDigest == silo.ImagePlatformDigest)
            ?? throw InstallerException.Refused("The recorded Silo image is not a platform of the pinned release.");
        var image = new SiloImageIdentity(silo.ImageReference, silo.ImageId, platform.Architecture, silo.ImagePlatformDigest, silo.ImageRelease,
            silo.ImageRevision);
        var observed = await VerifyContainerAsync(docker, instanceId, container, expectedRoot, image, expectApplication: true, cancellationToken)
            .ConfigureAwait(false);
        await docker.EnsureSiloDataRootExclusiveAsync(expectedRoot, container, cancellationToken).ConfigureAwait(false);
        var (access, secret) = ReadWriterCredential(paths);
        var cameraAgent = await VerifyApplicationAsync(docker, paths, instanceId, silo, application, cancellationToken).ConfigureAwait(false);
        var verified = SiloRuntimeProfile.ParseScriptResult(
            await docker.RunInApplicationNetworkAsync(cameraAgent.Id, observed.ImageId!, platform.Platform, observed.User!, instanceId,
                SiloRuntimeProfile.VerifyBindingScript(silo.Bucket, silo.MarkerVersionId,
                    SiloRuntimeProfile.ExpectSettings(Path.Combine(paths.ConfigRoot, "secrets"), SiloRuntimeProfile.BindingSettings(silo))),
                cancellationToken)
                .ConfigureAwait(false),
            "verified",
            "binding verification");
        // What CameraAgent's live settings mount gives it must be this instance's endpoint and the credential on record.
        if (verified.GetProperty("endpoint").GetString() != SiloRuntimeProfile.Endpoint)
            throw InstallerException.Refused("The Silo endpoint CameraAgent is configured with is not this instance's Silo endpoint.");
        if (verified.GetProperty("writerAccessKeyId").GetString() != access ||
            verified.GetProperty("writerSecretSha256").GetString() != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret))))
            throw InstallerException.Refused("The Silo writer credential CameraAgent holds is not the credential this instance records.");
        var marker = Convert.FromBase64String(verified.GetProperty("marker").GetString() ?? string.Empty);
        if (verified.GetProperty("markerSha256").GetString() != silo.MarkerSha256 ||
            Convert.ToHexStringLower(SHA256.HashData(marker)) != silo.MarkerSha256 ||
            !SiloRuntimeProfile.MarkerBinds(marker, instanceId))
            throw InstallerException.Refused("The Silo store's instance marker does not bind this instance.");
        // The fence in the store, not the install record, names the current writer: a qualified rotation replaces the
        // fence's key within the same generation (runbook). The install-time fence is still checked when it is current.
        var fenceVersion = verified.GetProperty("fenceVersionId").GetString() ?? string.Empty;
        var fence = Convert.FromBase64String(verified.GetProperty("fence").GetString() ?? string.Empty);
        if (fenceVersion == silo.FenceVersionId && Convert.ToHexStringLower(SHA256.HashData(fence)) != silo.FenceSha256)
            throw InstallerException.Refused("The Silo store's fence is not the bytes it was installed with.");
        var fenceWriter = SiloRuntimeProfile.FenceWriter(fence, instanceId, silo.WriterGeneration);
        if (fenceWriter is null)
            throw InstallerException.Refused("The Silo store's fence is not a writer fence of this instance at its generation.");
        if (fenceWriter != access)
            throw InstallerException.Refused("The writer credential CameraAgent holds is not the one the fence names.");
        var archive = silo.Archive is { } configured ? await ProbeArchiveAsync(paths, configured, cancellationToken).ConfigureAwait(false) : null;
        return new SiloVerification(
            SiloRuntimeProfile.HeadroomOf(expectedRoot),
            fenceVersion,
            access,
            Rotated: fenceVersion != silo.FenceVersionId,
            archive);
    }

    /// <summary>
    /// A release a Silo-layout install may use names exactly the Silo image this installer qualifies: an older release
    /// names none, and a release that pins another image was qualified by another installer. Both refuse before mutation.
    /// </summary>
    public static void AdmitRelease(ReleaseManifest release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!release.NamesSiloImage)
            throw InstallerException.Refused(
                $"Release {release.Version} predates the Silo layout: it names no stock Silo image. Nothing was changed.");
        if (!release.SiloImageMatchesPin)
            throw InstallerException.Refused(
                $"Release {release.Version} names a stock Silo image this installer has not qualified. Nothing was changed.");
    }

    /// <summary>The label a CameraAgent image declares when it honors the installer's first-boot pause (#1966).</summary>
    public const string FirstBootContractLabel = "io.hvo.skymonitor.first-boot-contract";

    public const string FirstBootContract = "cameraagent-first-boot-pause-v1";

    /// <summary>
    /// Refuses a CameraAgent image that does not declare the first-boot pause contract: it would ignore the seeded pause
    /// and admit captures. An image not yet on the daemon is checked once it is prepared, unless <paramref name="required"/>.
    /// </summary>
    public static Task EnsureFirstBootCapableAsync(DockerClient docker, string reference, bool required, CancellationToken cancellationToken)
        => EnsureFirstBootCapableAsync(docker, [reference], required, cancellationToken);

    /// <summary>
    /// Checks the first of <paramref name="references"/> the daemon holds, since Docker's stores name one image differently;
    /// when it holds none, only a <paramref name="required"/> check refuses.
    /// </summary>
    public static async Task EnsureFirstBootCapableAsync(
        DockerClient docker, IEnumerable<string?> references, bool required, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentNullException.ThrowIfNull(references);
        foreach (var reference in references)
        {
            if (string.IsNullOrEmpty(reference)) continue;
            var (present, contract) = await docker.TryReadImageLabelAsync(reference, FirstBootContractLabel, cancellationToken).ConfigureAwait(false);
            if (!present) continue;
            if (contract != FirstBootContract)
                throw InstallerException.Refused(
                    "The CameraAgent image does not declare the first-boot pause contract a Silo-layout install needs. Nothing was changed.");
            return;
        }
        if (required)
            throw InstallerException.Refused("The CameraAgent image a Silo-layout install needs is not on this Docker daemon.");
    }

    private const string ArchiveDirectoryName = "silo-archive";
    private const string ArchiveAuthorityFile = "authority.pem";
    private const string ArchiveCredentialFile = "credential";

    /// <summary>
    /// The optional archive's read-only admission (#1966): the endpoint answers over TLS that chains to the configured
    /// authority for its own name, the credential authenticates, and the bucket is versioned. Anything else refuses the
    /// install before mutation; an archive that cannot be reached cannot be verified, so it refuses as well.
    /// </summary>
    public static async Task<SiloArchiveIdentity> AdmitArchiveAsync(InstallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var endpoint = SiloArchiveProbe.ValidateEndpoint(request.SiloArchiveEndpoint!, request.SiloArchiveBucket!);
        var pem = SiloArchiveProbe.ReadAuthorityText(request.SiloArchiveCaFile!);
        var authority = SiloArchiveProbe.ParseAuthority(pem);
        var credential = SiloArchiveProbe.ReadCredential(request.SiloArchiveCredentialFile!);
        var observed = await SiloArchiveProbe.ProbeAsync(
            endpoint, request.SiloArchiveBucket!, authority, credential, TimeProvider.System, cancellationToken).ConfigureAwait(false);
        if (observed.State != SiloArchiveState.Verified || observed.ServerCertificateSha256 is null)
            throw InstallerException.Refused($"The Silo archive configuration could not be verified ({observed.Reason}). Nothing was changed.");
        return new SiloArchiveIdentity(
            endpoint.GetLeftPart(UriPartial.Authority),
            request.SiloArchiveBucket!,
            SiloArchiveProbe.AuthoritySha256(pem),
            credential.AccessKey,
            observed.ServerCertificateSha256,
            DateTimeOffset.UtcNow);
    }

    /// <summary>Copies the verified archive authority and credential into the instance's owner-only configuration; mounts them nowhere.</summary>
    public static void PersistArchiveConfiguration(InstallationPaths paths, InstallRequest request, SiloArchiveIdentity archive)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(archive);
        var directory = Path.Combine(paths.ConfigRoot, ArchiveDirectoryName);
        SafeFileSystem.CreateOwnerDirectory(directory);
        var authority = SiloArchiveProbe.ReadAuthorityText(request.SiloArchiveCaFile!);
        if (SiloArchiveProbe.AuthoritySha256(authority) != archive.AuthoritySha256)
            throw new InstallerException("The archive certificate authority file changed after it was verified.");
        SafeFileSystem.WriteTextAtomic(Path.Combine(directory, ArchiveAuthorityFile), authority);
        var (access, secret) = SiloArchiveProbe.ReadCredential(request.SiloArchiveCredentialFile!);
        if (access != archive.AccessKeyId)
            throw new InstallerException("The archive credential file changed after it was verified.");
        SafeFileSystem.WriteTextAtomic(Path.Combine(directory, ArchiveCredentialFile), $"{access}\n{secret}\n");
    }

    /// <summary>Probes the recorded archive with the instance's own copies of its authority and credential. Read-only.</summary>
    public static async Task<SiloArchiveObservation> ProbeArchiveAsync(
        InstallationPaths paths, SiloArchiveIdentity archive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var directory = Path.Combine(paths.ConfigRoot, ArchiveDirectoryName);
        var authorityPath = Path.Combine(directory, ArchiveAuthorityFile);
        SafeFileSystem.ValidateOwnerFile(authorityPath);
        var pem = SiloArchiveProbe.ReadAuthorityText(authorityPath);
        if (SiloArchiveProbe.AuthoritySha256(pem) != archive.AuthoritySha256)
            return new SiloArchiveObservation(SiloArchiveState.Refused, "archive-authority-changed", null);
        var credential = SiloArchiveProbe.ReadCredential(Path.Combine(directory, ArchiveCredentialFile));
        if (credential.AccessKey != archive.AccessKeyId)
            return new SiloArchiveObservation(SiloArchiveState.Refused, "archive-credential-changed", null);
        return await SiloArchiveProbe.ProbeAsync(
            new Uri(archive.Endpoint + "/"), archive.Bucket, SiloArchiveProbe.ParseAuthority(pem), credential, TimeProvider.System,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A completed Silo-layout instance's install re-run (#1966): with the inputs it was installed with, it verifies the
    /// runtime read-only and changes nothing; with any other input it refuses, because a Silo instance is never changed or
    /// upgraded in place, and names <c>silo-verify</c> as the way to check it.
    /// </summary>
    public static async Task<InstallationResult> ReRunAsync(
        DockerClient docker,
        InstallationPaths paths,
        Guid instanceId,
        bool sameInputs,
        InstanceManifest manifest,
        InstallationResult result,
        IReadOnlyList<InstallationWarning> warnings,
        CancellationToken cancellationToken)
    {
        var silo = AdmitReRun(instanceId, sameInputs, manifest, result);
        var verification = await VerifyAsync(docker, paths, instanceId, silo, manifest.Image, cancellationToken).ConfigureAwait(false);
        if (verification.Readiness == "not-ready")
            throw InstallerException.Refused(
                "This Silo-layout instance is installed but not ready: its data-root headroom is below the reserve or its archive store " +
                $"failed verification. Nothing was changed. Check it with: hvo-sky-install cameraagent silo-verify --instance-id {instanceId:D}");
        var items = new List<DeploymentPlanItem>
        {
            new(DeploymentPlanAction.Unchanged, "cameraagent", ComposeDeployment.ContainerNameFor(instanceId), "running, healthy, recorded image"),
            new(DeploymentPlanAction.Unchanged, "silo-store", silo.Bucket, "binding verified from CameraAgent's network"),
            new(DeploymentPlanAction.Unchanged, "silo-data-root", silo.DataRoot,
                verification.Headroom.Ready ? "headroom ready" : "headroom below the reserve; run silo-verify")
        };
        if (verification.Archive is { } archive)
            items.Add(new(DeploymentPlanAction.Unchanged, "silo-archive", silo.Archive!.Endpoint, archive.Reason));
        return result with { Warnings = warnings, Plan = DeploymentPlan.Of(items, "the Silo-layout instance verified") };
    }

    /// <summary>
    /// The plan of a completed Silo-layout instance's install re-run (#1966 review): the run's refusals, and with the
    /// inputs it was installed with, what the run verifies, all unchanged. It asks Docker nothing and starts no container;
    /// the run verifies read-only, from CameraAgent's network.
    /// </summary>
    public static InstallationResult PlanReRun(
        Guid instanceId,
        bool sameInputs,
        InstanceManifest manifest,
        InstallationResult result,
        IReadOnlyList<InstallationWarning> warnings)
    {
        var silo = AdmitReRun(instanceId, sameInputs, manifest, result);
        var items = new List<DeploymentPlanItem>
        {
            new(DeploymentPlanAction.Unchanged, "cameraagent", ComposeDeployment.ContainerNameFor(instanceId),
                "the run verifies it is running, healthy and on the recorded image"),
            new(DeploymentPlanAction.Unchanged, "container", SiloRuntimeProfile.ContainerNameFor(instanceId),
                $"the run verifies the pinned Silo {silo.ImageRelease} image and its runtime shape"),
            new(DeploymentPlanAction.Unchanged, "network", SiloRuntimeProfile.NetworkNameFor(instanceId), "the run verifies it is internal"),
            new(DeploymentPlanAction.Unchanged, "silo-store", silo.Bucket, "the run verifies the binding from CameraAgent's network"),
            new(DeploymentPlanAction.Unchanged, "silo-data-root", silo.DataRoot, "the run verifies its filesystem and headroom")
        };
        if (silo.Archive is { } archive)
            items.Add(new(DeploymentPlanAction.Unchanged, "silo-archive", archive.Endpoint, "the run verifies it read-only"));
        return result with
        {
            Outcome = InstallationOutcome.Planned,
            Warnings = warnings,
            Plan = DeploymentPlan.Of(items, "the run verifies the Silo-layout instance")
        };
    }

    /// <summary>
    /// What a fresh or resumed Silo-layout install creates beside CameraAgent (#1966 review): the data root, the private
    /// network, the stock Silo container, the bound store and the persisted first-boot pause.
    /// </summary>
    public static IEnumerable<DeploymentPlanItem> PlanItems(
        InstallationPaths paths,
        Guid instanceId,
        SiloImagePin.PlatformImage platform,
        string imageSource,
        (string FileSystem, string Device) dataRoot,
        bool resumed)
    {
        ArgumentNullException.ThrowIfNull(platform);
        var made = resumed ? DeploymentPlanAction.Change : DeploymentPlanAction.Create;
        var dataRootPath = SiloRuntimeProfile.DataRoot(paths);
        yield return new(Directory.Exists(dataRootPath) ? DeploymentPlanAction.Unchanged : DeploymentPlanAction.Create, "folder", dataRootPath,
            $"Silo data root on local {dataRoot.FileSystem} (device {dataRoot.Device})");
        yield return new(made, "network", SiloRuntimeProfile.NetworkNameFor(instanceId), "internal: the Silo and CameraAgent containers only");
        yield return new(made, "container", SiloRuntimeProfile.ContainerNameFor(instanceId),
            $"stock Silo {SiloImagePin.Release} for {platform.Platform}, {imageSource}; no published port, console off");
        yield return new(made, "silo-store", SiloRuntimeProfile.BucketFor(instanceId), "versioned bucket, writer credential and instance binding");
        yield return new(made, "first-boot-pause", "capture admission",
            "persisted as paused before CameraAgent first starts; cameraagent first-resume releases it");
    }

    private static SiloInstallationIdentity AdmitReRun(Guid instanceId, bool sameInputs, InstanceManifest manifest, InstallationResult result)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(result);
        if (!sameInputs)
            throw InstallerException.Refused(
                "This Silo-layout instance is installed, and an install re-run never changes or upgrades it in place. Nothing was changed. " +
                $"Check it with: hvo-sky-install cameraagent silo-verify --instance-id {instanceId:D}");
        if (manifest.Silo is not { } silo || manifest.LifecycleCondition != InstanceLifecycleCondition.Installed)
            throw InstallerException.Refused("The retained instance is not an installed Silo-layout instance. Nothing was changed.");
        return silo;
    }

    /// <summary>
    /// Persists the first-boot pause before the capture service first starts (contract 8.1): while the CameraAgent
    /// service container does not exist, CameraAgent runs once in its initialize-only mode with this instance's mounts and
    /// settings and no web host, camera or capture loop, creating the raw-ingress journal whose schema transaction writes
    /// the pause. The installer then reads the database itself and proves the pause is durable.
    /// </summary>
    public static async Task PersistFirstBootPauseAsync(
        DockerClient docker,
        ComposeFiles compose,
        InstallationPaths paths,
        Guid firstBootPauseId,
        CancellationToken cancellationToken)
    {
        var service = await docker.InspectContainerAsync(compose.ContainerName, cancellationToken).ConfigureAwait(false);
        if (!service.Exists)
        {
            var initialized = await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName,
                    ["run", "--rm", "--no-deps", "--no-TTY", "cameraagent", "--hvo-initialize-first-boot-state"], cancellationToken)
                .ConfigureAwait(false);
            _ = SiloRuntimeProfile.ParseScriptResult(initialized, "seeded", "first-boot pause");
        }
        await VerifyPersistedFirstBootPauseAsync(paths.StateRoot, firstBootPauseId, requireInForce: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the instance's raw-ingress journal read-only and proves the first-boot pause is durable: the seeded command
    /// exists exactly as the app writes it and, when <paramref name="requireInForce"/>, it is still the latest command
    /// with capture paused at version 1.
    /// </summary>
    public static async Task VerifyPersistedFirstBootPauseAsync(
        string stateRoot, Guid firstBootPauseId, bool requireInForce, CancellationToken cancellationToken)
    {
        var database = CameraAgentStateLayout.RawIngressDatabasePath(stateRoot);
        if (!File.Exists(database))
            throw new InstallerException("The first-boot pause has not been persisted.");
        var pauseKey = $"installer:first-boot:{firstBootPauseId:D}:pause";
        var actor = $"installer-first-boot:{firstBootPauseId:D}";
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.state, s.version,
                   (SELECT COUNT(*) FROM capture_control_commands c
                     WHERE c.idempotency_key = $key AND c.actor = $actor AND c.target_state = 'paused'
                       AND c.expected_version = 0 AND c.status = 'completed' AND c.result_state = 'paused'
                       AND c.result_version = 1 AND c.changed = 1),
                   (SELECT idempotency_key FROM capture_control_commands ORDER BY rowid DESC LIMIT 1)
            FROM capture_control_state s WHERE s.state_key = 1;
            """;
        command.Parameters.AddWithValue("$key", pauseKey);
        command.Parameters.AddWithValue("$actor", actor);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InstallerException("The capture control state is missing.");
        var seeded = reader.GetInt64(2) == 1;
        var inForce = seeded && reader.GetString(0) == "paused" && reader.GetInt64(1) == 1 &&
                      !await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) && reader.GetString(3) == pauseKey;
        if (!seeded || requireInForce && !inForce)
            throw new InstallerException("The persisted capture state is not this instance's first-boot pause.");
    }

    /// <summary>
    /// The Silo container as qualified (#1966; contract 2 and 7.2): running and healthy as this daemon's own platform child
    /// of the pinned release, publishing nothing, unprivileged with a read-only root, console off, disk sync on, on this
    /// instance's data root, and joined only to the instance's own private network under its alias. That network must be
    /// the instance's Compose network, internal, labelled as the instance's, and hold no running container but this Silo
    /// and (once it runs, <paramref name="expectApplication"/>) this instance's CameraAgent. Read-only; returns what it saw.
    /// </summary>
    internal static async Task<SiloContainerObservation> VerifyContainerAsync(
        DockerClient docker,
        Guid instanceId,
        string container,
        string dataRoot,
        SiloImageIdentity image,
        bool expectApplication,
        CancellationToken cancellationToken)
    {
        var observed = await docker.ObserveSiloContainerAsync(container, cancellationToken).ConfigureAwait(false)
            ?? throw InstallerException.Refused("The Silo container does not exist.");
        var platform = SiloImagePin.Platforms.FirstOrDefault(candidate => candidate.ManifestDigest == image.PlatformDigest)
            ?? throw InstallerException.Refused("The recorded Silo image is not a platform of the pinned release.");
        var failures = new List<string>();
        if (!observed.Running || !observed.Healthy) failures.Add("not running and healthy");
        if (!SiloImagePin.RunsPinnedChild(observed.ImageId, observed.ImageManifestDigest, observed.ImageManifestArchitecture,
                observed.ImageManifestOperatingSystem, platform))
            failures.Add($"not the pinned image for {platform.Platform}");
        if (observed.PublishesPorts) failures.Add("publishes a host port");
        if (!observed.ReadOnlyRootFileSystem) failures.Add("root filesystem is writable");
        if (observed.User is null || observed.User.StartsWith("0:", StringComparison.Ordinal) || observed.User == "root")
            failures.Add("runs as root");
        if (!observed.ConsoleDisabled) failures.Add("console not disabled");
        if (!observed.DiskSyncOnWrite) failures.Add("disk sync not enabled");
        if (observed.DataRootSource != dataRoot) failures.Add("data root is not the instance's");
        failures.AddRange(await PrivateNetworkFailuresAsync(docker, instanceId, observed, expectApplication, cancellationToken).ConfigureAwait(false));
        if (failures.Count != 0)
            throw InstallerException.Refused($"The Silo container does not match its qualified profile: {string.Join("; ", failures)}.");
        return observed;
    }

    /// <summary>What is wrong with the Silo container's private network, if anything. Read-only; inspection failures fail closed.</summary>
    private static async Task<IReadOnlyList<string>> PrivateNetworkFailuresAsync(
        DockerClient docker, Guid instanceId, SiloContainerObservation silo, bool expectApplication, CancellationToken cancellationToken)
    {
        var name = SiloRuntimeProfile.NetworkNameFor(instanceId);
        var project = ComposeDeployment.ContainerNameFor(instanceId);
        if (silo.Networks is not [var only] || only != name)
            return ["not joined to the instance's private network alone"];
        if (!silo.NetworkAliases.TryGetValue(name, out var aliases) || !aliases.Contains(SiloRuntimeProfile.NetworkAlias, StringComparer.Ordinal))
            return [$"not reachable as {SiloRuntimeProfile.NetworkAlias} on the private network"];
        DockerNetworkObservation? network;
        try
        {
            network = await docker.InspectNetworkAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (InstallerException)
        {
            return ["private network could not be inspected"];
        }
        if (network is null) return ["private network does not exist"];
        string? Label(string label) => network.Labels.TryGetValue(label, out var value) ? value : null;
        if (!network.Internal || network.Driver != "bridge") return ["private network is not an internal bridge network"];
        if (Label("com.docker.compose.project") != project || Label("com.docker.compose.network") != SiloRuntimeProfile.NetworkName ||
            Label("io.hvo.skymonitor.instance-id") != instanceId.ToString("D") || Label("io.hvo.skymonitor.network") != SiloRuntimeProfile.NetworkName)
            return ["private network is not labelled as this instance's"];
        var application = await docker.ObserveApplicationContainerAsync(project, cancellationToken).ConfigureAwait(false);
        var allowed = new HashSet<string>(StringComparer.Ordinal) { silo.ContainerId ?? string.Empty };
        if (application is not null) allowed.Add(application.Id);
        if (!network.ContainerIds.Contains(silo.ContainerId ?? string.Empty)) return ["Silo is not a member of the private network"];
        if (network.ContainerIds.Any(member => !allowed.Contains(member)))
            return ["private network holds a container that is not this instance's"];
        if (expectApplication && (application is null || !application.Running || !network.ContainerIds.Contains(application.Id) ||
                                  !application.Networks.Contains(name, StringComparer.Ordinal)))
            return ["CameraAgent is not a running member of the private network"];
        return [];
    }

    /// <summary>
    /// The running CameraAgent as it is now (#1966 review): this instance's container, running, healthy, on the image the
    /// instance records, and started after its Silo writer credential was last written, so the running process holds the
    /// credential the files hold. Read-only.
    /// </summary>
    private static async Task<ApplicationContainerObservation> VerifyApplicationAsync(
        DockerClient docker, InstallationPaths paths, Guid instanceId, SiloInstallationIdentity silo, ImageInstallationIdentity application,
        CancellationToken cancellationToken)
    {
        var project = ComposeDeployment.ContainerNameFor(instanceId);
        var observed = await docker.ObserveApplicationContainerAsync(project, cancellationToken).ConfigureAwait(false)
            ?? throw InstallerException.Refused("CameraAgent is not running: its container does not exist.");
        if (!observed.Running)
            throw InstallerException.Refused("CameraAgent is not running.");
        if (!string.Equals(observed.ImageId, application.ImageId, StringComparison.Ordinal))
            throw InstallerException.Refused("CameraAgent is running an image other than the one this instance records.");
        if (observed.Health != "healthy")
            throw InstallerException.Refused($"CameraAgent is not healthy now ({(observed.Health is { Length: <= 16 } health ? health : "no health status")}).");
        if (!observed.Labels.TryGetValue("com.docker.compose.project", out var labelledProject) || labelledProject != project ||
            !observed.Labels.TryGetValue("io.hvo.skymonitor.instance-id", out var labelledInstance) || labelledInstance != instanceId.ToString("D"))
            throw InstallerException.Refused("CameraAgent's container is not labelled as this instance's.");
        var secrets = Path.Combine(paths.ConfigRoot, "secrets");
        // The settings CameraAgent consumes: this instance's settings directory, mounted read-only where it reads its
        // setting files, and no environment variable overriding a Silo setting.
        var settingsMounts = observed.Mounts.Where(static mount => mount.Destination == SiloRuntimeProfile.CameraAgentSettingsMount).ToArray();
        if (settingsMounts is not [var settingsMount] || settingsMount.Type != "bind" || settingsMount.Writable ||
            !string.Equals(settingsMount.Source, secrets, StringComparison.Ordinal))
            throw InstallerException.Refused("CameraAgent does not consume this instance's settings: its settings mount is missing, writable or elsewhere.");
        if (!observed.Environment.Contains($"HVO_KEY_PER_FILE_DIRECTORY={SiloRuntimeProfile.CameraAgentSettingsMount}", StringComparer.Ordinal) ||
            observed.Environment.Any(static variable => variable.StartsWith("CameraAgent__Silo__", StringComparison.Ordinal)))
            throw InstallerException.Refused("CameraAgent does not consume this instance's Silo settings: its environment overrides or omits them.");
        var endpoint = Path.Combine(secrets, "CameraAgent__Silo__Endpoint");
        if (!File.Exists(endpoint) ||
            await File.ReadAllTextAsync(endpoint, cancellationToken).ConfigureAwait(false) != SiloRuntimeProfile.Endpoint)
            throw InstallerException.Refused("CameraAgent is not configured with this instance's Silo endpoint.");
        // The active credential pair was published when its later file was renamed into place: a rename keeps a staged
        // file's modification time but advances its change time, so only a start after both changes proves the running
        // process read this exact pair.
        var published = new[] { SiloRuntimeProfile.WriterAccessKeySetting, SiloRuntimeProfile.WriterSecretKeySetting }
            .Select(setting => NativeLinux.GetChangeTimeUtc(Path.Combine(secrets, setting)))
            .Max();
        if (observed.StartedUtc is not { } started || started <= published)
            throw InstallerException.Refused(
                "CameraAgent has not restarted since its Silo writer credential was written, so it may not hold that credential. Restart it.");
        // CameraAgent reads its setting files once, when it starts (#1966 review). The bucket and binding just verified are
        // the ones it runs with only if none of those files, nor the endpoint, changed after it started.
        var configured = SiloRuntimeProfile.BindingSettings(silo).Keys.Append(SiloRuntimeProfile.EndpointSetting)
            .Select(setting => NativeLinux.GetChangeTimeUtc(Path.Combine(secrets, setting)))
            .Max();
        if (started <= configured)
            throw InstallerException.Refused(
                "CameraAgent has not restarted since its Silo bucket, binding or endpoint settings changed, so it may still use the " +
                "earlier ones. Restart it, then verify again.");
        return observed;
    }

    private static System.Text.Json.Nodes.JsonObject BootstrapIntent(Guid instanceId, string dataRoot, string dataRootDevice) => new()
    {
        ["format"] = IntentFormat,
        ["instanceId"] = instanceId.ToString("D"),
        ["storeId"] = SiloRuntimeProfile.StoreIdFor(instanceId),
        ["bucket"] = SiloRuntimeProfile.BucketFor(instanceId),
        ["dataRoot"] = dataRoot,
        ["dataRootDevice"] = dataRootDevice,
        ["writerGeneration"] = SiloRuntimeProfile.WriterGeneration,
        ["storePreflight"] = "empty"
    };

    private static string CredentialRecord(Guid instanceId, string bucket, string access, string secret) => new System.Text.Json.Nodes.JsonObject
    {
        ["format"] = CredentialFormat,
        ["instanceId"] = instanceId.ToString("D"),
        ["bucket"] = bucket,
        ["writerGeneration"] = SiloRuntimeProfile.WriterGeneration,
        ["accessKey"] = access,
        ["secretKey"] = secret
    }.ToJsonString();

    /// <summary>An owner-only bootstrap record, or null when there is none; an unreadable one is ambiguous and refused.</summary>
    private static System.Text.Json.Nodes.JsonNode? ReadRecord(string path)
    {
        if (!File.Exists(path)) return null;
        SafeFileSystem.ValidateOwnerFile(path);
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException exception)
        {
            throw InstallerException.Refused("A retained Silo bootstrap record is not readable. Nothing was changed in the store.", exception);
        }
    }

    private static (string Access, string Secret)? ReadCredentialRecord(string path, Guid instanceId, string bucket)
    {
        if (ReadRecord(path) is not System.Text.Json.Nodes.JsonObject record) return null;
        var access = (string?)record["accessKey"];
        var secret = (string?)record["secretKey"];
        if ((string?)record["format"] != CredentialFormat || (string?)record["instanceId"] != instanceId.ToString("D") ||
            (string?)record["bucket"] != bucket || (int?)record["writerGeneration"] != SiloRuntimeProfile.WriterGeneration ||
            access is null || secret is null || !SiloRuntimeProfile.AccessKeyPattern().IsMatch(access) ||
            !SiloRuntimeProfile.SecretKeyPattern().IsMatch(secret) || record.Count != 6)
            throw InstallerException.Refused("The retained Silo writer credential record is not this instance's. Nothing was changed in the store.");
        return (access, secret);
    }

    /// <summary>
    /// Refuses unless each setting file that binds CameraAgent to its store is the instance's own regular file holding
    /// exactly the value its manifest records (#1966 review). Nothing is changed.
    /// </summary>
    internal static void EnsureConfiguredBinding(InstallationPaths paths, SiloInstallationIdentity silo)
    {
        var secrets = Path.Combine(paths.ConfigRoot, "secrets");
        foreach (var (name, expected) in SiloRuntimeProfile.BindingSettings(silo))
        {
            var path = Path.Combine(secrets, name);
            string? configured;
            try
            {
                SafeFileSystem.ValidateOwnerFile(path);
                configured = File.ReadAllText(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InstallerException)
            {
                configured = null;
            }
            if (!string.Equals(configured, expected, StringComparison.Ordinal))
                throw InstallerException.Refused(name == SiloRuntimeProfile.BucketSetting
                    ? "The Silo bucket CameraAgent is configured with is not this instance's bucket. Nothing was changed."
                    : $"CameraAgent's Silo setting {name} is not the one this instance records. Nothing was changed.");
        }
    }

    private static (string Access, string Secret) ReadWriterCredential(InstallationPaths paths)
    {
        var secrets = Path.Combine(paths.ConfigRoot, "secrets");
        var accessPath = Path.Combine(secrets, SiloRuntimeProfile.WriterAccessKeySetting);
        var secretPath = Path.Combine(secrets, SiloRuntimeProfile.WriterSecretKeySetting);
        SafeFileSystem.ValidateOwnerFile(accessPath);
        SafeFileSystem.ValidateOwnerFile(secretPath);
        var access = File.ReadAllText(accessPath).Trim();
        var secret = File.ReadAllText(secretPath).Trim();
        if (!SiloRuntimeProfile.AccessKeyPattern().IsMatch(access) || !SiloRuntimeProfile.SecretKeyPattern().IsMatch(secret))
            throw new InstallerException("The retained Silo writer credential is not in the expected form.");
        return (access, secret);
    }

    private static byte[] FixedBytes(string path, Func<byte[]> create)
    {
        if (File.Exists(path))
        {
            SafeFileSystem.ValidateOwnerFile(path);
            return File.ReadAllBytes(path);
        }
        var bytes = create();
        SafeFileSystem.WriteTextAtomic(path, Encoding.UTF8.GetString(bytes));
        return File.ReadAllBytes(path);
    }

    private static bool FenceNames(byte[] fence, string access)
    {
        try
        {
            using var document = JsonDocument.Parse(fence);
            return document.RootElement.TryGetProperty("writerAccessKeyId", out var value) && value.GetString() == access &&
                   document.RootElement.TryGetProperty("writerGeneration", out var generation) &&
                   generation.GetInt32() == SiloRuntimeProfile.WriterGeneration;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>What a read-only Silo verification found besides the checks that refuse (#1966).</summary>
internal sealed record SiloVerification(
    SiloHeadroom Headroom,
    string FenceVersionId,
    string WriterAccessKeyId,
    bool Rotated,
    SiloArchiveObservation? Archive)
{
    /// <summary>
    /// <c>ready</c>; <c>degraded</c> when only the optional archive cannot be reached (local capture is unaffected); or
    /// <c>not-ready</c> when the data-root headroom is below the reserve or the archive answers but is not the configured,
    /// trusted, authorized and versioned store.
    /// </summary>
    public string Readiness => !Headroom.Ready || Archive is { State: SiloArchiveState.Refused }
        ? "not-ready"
        : Archive is { State: SiloArchiveState.TemporarilyUnavailable } ? "degraded" : "ready";
}
