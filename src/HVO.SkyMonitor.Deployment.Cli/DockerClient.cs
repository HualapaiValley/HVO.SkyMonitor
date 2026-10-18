using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The daemon identity and the host capabilities <c>docker info</c> reports that decide whether the CameraAgent
/// Compose resource limits are enforced, whether the Docker host has the CPUs and memory they name, and where Docker
/// stores images. A capability the daemon does not report is <see langword="null"/> and is treated as unsupported.
/// </summary>
internal sealed record DockerHostCapabilities(
    DockerDaemonIdentity Daemon,
    bool? MemoryLimit,
    bool? CpuCfsQuota,
    bool? PidsLimit,
    string? CgroupVersion,
    string? DockerRootDir,
    IReadOnlyList<string> Warnings,
    int? CpuCount = null,
    long? MemoryBytes = null);

internal sealed class DockerClient(IProcessRunner processRunner)
{
    private string? endpoint;

    internal sealed record ContainerRuntimeIdentity(bool Exists, bool Running, bool Healthy, string? ImageId);
    public async Task<DockerDaemonIdentity> PreflightAsync(CancellationToken cancellationToken)
    {
        var identity = await ReadDaemonIdentityAsync(cancellationToken).ConfigureAwait(false);
        await RunDockerAsync(["compose", "version", "--short"], cancellationToken).ConfigureAwait(false);
        return identity;
    }

    /// <summary>
    /// The installation preflight: the daemon identity, the Compose plugin, and the capabilities the host
    /// prerequisites evaluate, all from one <c>docker info</c> read. Lifecycle operations keep using
    /// <see cref="PreflightAsync"/>, so uninstall and purge never depend on these capabilities.
    /// </summary>
    public async Task<DockerHostCapabilities> ReadHostCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var daemonResult = await RunDockerAsync(["info", "--format", "{{json .}}"], cancellationToken).ConfigureAwait(false);
        DockerHostCapabilities capabilities;
        using (var daemonJson = JsonDocument.Parse(daemonResult.StandardOutput))
        {
            var daemon = daemonJson.RootElement;
            capabilities = new DockerHostCapabilities(
                ParseDaemonIdentity(daemon),
                OptionalBoolean(daemon, "MemoryLimit"),
                OptionalBoolean(daemon, "CpuCfsQuota"),
                OptionalBoolean(daemon, "PidsLimit"),
                OptionalString(daemon, "CgroupVersion"),
                OptionalString(daemon, "DockerRootDir"),
                daemon.TryGetProperty("Warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array
                    ? warnings.EnumerateArray()
                        .Where(static warning => warning.ValueKind == JsonValueKind.String)
                        .Select(static warning => warning.GetString()!)
                        .ToArray()
                    : [],
                daemon.TryGetProperty("NCPU", out var cpus) && cpus.ValueKind == JsonValueKind.Number &&
                cpus.TryGetInt32(out var cpuCount) && cpuCount > 0
                    ? cpuCount
                    : null,
                daemon.TryGetProperty("MemTotal", out var memory) && memory.ValueKind == JsonValueKind.Number &&
                memory.TryGetInt64(out var memoryBytes) && memoryBytes > 0
                    ? memoryBytes
                    : null);
        }
        await RunDockerAsync(["compose", "version", "--short"], cancellationToken).ConfigureAwait(false);
        return capabilities;
    }

    private static bool? OptionalBoolean(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string? OptionalString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Loads, pulls or finds the CameraAgent image a request names and reads its identity and contract labels. A
    /// <paramref name="release"/> selection additionally requires the image Docker resolved to be the release's: one of
    /// the identities its offline archive names, and labelled with the release's version and commit. A release image
    /// this call imports is removed again when it is refused, when Docker fails, or when the operation is cancelled, so
    /// a refused or abandoned release leaves Docker holding what it held.
    /// </summary>
    public async Task<(DockerDaemonIdentity Daemon, ImageInstallationIdentity Image)> PrepareImageAsync(
        InstallRequest request,
        bool allowMutation,
        ReleaseImageSelection? release,
        CancellationToken cancellationToken)
    {
        var added = allowMutation && release is not null
            ? await AbsentAsync(ReleaseIdentities(release), cancellationToken).ConfigureAwait(false)
            : [];
        try
        {
            return await PrepareImageCoreAsync(request, allowMutation, release, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (added.Count > 0)
        {
            // A cancellation is removed too: a load or pull it interrupted may already have imported the image.
            await RemoveImagesAsync(added).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Imports a release's image, loading its offline archive or pulling it by the digest release.json names, and checks
    /// what Docker then holds with <paramref name="validate"/> as well as against the release, before anything else is
    /// changed for it. Whatever this import added is removed again when the image is refused, when Docker fails, or when
    /// the operation is cancelled, so it leaves Docker holding what it held. A registry image's metadata must have been
    /// read and checked first (<see cref="ReadRegistryReleaseImageAsync"/>); these checks after the import are the second.
    /// </summary>
    public async Task<ImageInstallationIdentity> ImportReleaseImageAsync(
        InstallRequest request,
        ReleaseImageSelection release,
        Action<ImageInstallationIdentity> validate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(validate);
        var added = await AbsentAsync(ReleaseIdentities(release), cancellationToken).ConfigureAwait(false);
        try
        {
            var (_, image) = await PrepareImageCoreAsync(request, allowMutation: true, release, cancellationToken)
                .ConfigureAwait(false);
            validate(image);
            return image;
        }
        catch (Exception) when (added.Count > 0)
        {
            await RemoveImagesAsync(added).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Reads a release's image from its registry, by the digest release.json names, and checks it without importing
    /// anything (<see cref="ReleaseRegistryImage"/>). Returns the image's configuration identity, which the caller holds to
    /// its own requirements before anything is pulled, and the selection extended by every identity the registry gave.
    /// </summary>
    public Task<(ImageInstallationIdentity Image, ReleaseImageSelection Release)> ReadRegistryReleaseImageAsync(
        ReleaseImageSelection release,
        CancellationToken cancellationToken)
        => ReleaseRegistryImage.ReadAsync(release, RunDockerRawAsync, cancellationToken);

    /// <summary>The names Docker can give a release's image: its digest reference, then its archive's identities.</summary>
    private static string[] ReleaseIdentities(ReleaseImageSelection release)
        => [.. new[] { release.DigestReference }.Concat(release.ImageIds ?? []).Distinct(StringComparer.Ordinal)];

    private async Task<List<string>> AbsentAsync(IEnumerable<string> references, CancellationToken cancellationToken)
    {
        var absent = new List<string>();
        foreach (var reference in references)
        {
            if (await TryInspectImageAsync(reference, cancellationToken).ConfigureAwait(false) is null)
            {
                absent.Add(reference);
            }
        }
        return absent;
    }

    /// <summary>
    /// Removes what an import added, best effort and without force: a name another image or a container now uses is
    /// left alone. It is never cancelled, so it also runs after a cancellation and a refused image is not left half
    /// removed.
    /// </summary>
    private async Task RemoveImagesAsync(IEnumerable<string> references)
    {
        foreach (var reference in references)
        {
            _ = await RunDockerRawAsync(["image", "rm", reference], CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<(DockerDaemonIdentity Daemon, ImageInstallationIdentity Image)> PrepareImageCoreAsync(
        InstallRequest request,
        bool allowMutation,
        ReleaseImageSelection? release,
        CancellationToken cancellationToken)
    {
        var identity = await ReadDaemonIdentityAsync(cancellationToken).ConfigureAwait(false);
        var architecture = identity.Architecture;
        await RunDockerAsync(["compose", "version", "--short"], cancellationToken).ConfigureAwait(false);
        HashSet<string>? loadedImageIds = null;

        if (request.ImageArchive is not null)
        {
            await using var archive = SafeFileSystem.OpenRegularFileRead(request.ImageArchive);
            var actual = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(
                archive,
                cancellationToken).ConfigureAwait(false));
            if (!string.Equals(actual, request.ImageArchiveSha256, StringComparison.Ordinal))
            {
                throw new InstallerException("The image archive SHA-256 does not match the trusted input.");
            }
            if (allowMutation)
            {
                var descriptorPath = $"/proc/{Environment.ProcessId}/fd/{archive.SafeFileHandle.DangerousGetHandle().ToInt64()}";
                var load = await RunDockerAsync(["image", "load", "--input", descriptorPath], cancellationToken)
                    .ConfigureAwait(false);
                loadedImageIds = await ReadLoadedImageIdsAsync(load.StandardOutput, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (allowMutation && !request.NoDownload && request.ImageReference.Contains('@', StringComparison.Ordinal))
        {
            await RunDockerAsync(["image", "pull", request.ImageReference], cancellationToken).ConfigureAwait(false);
        }

        // A daemon backed by the containerd image store addresses a loaded image by its index or manifest digest, while
        // the classic store addresses it by its configuration digest. A release's offline archive names all three, so
        // resolve the candidate the daemon actually knows instead of assuming one store's identity scheme.
        var reference = request.ImageReference;
        var inspectResult = reference.Length == 0
            ? null
            : await TryInspectImageAsync(reference, cancellationToken).ConfigureAwait(false);
        if (inspectResult is null && release?.ImageIds is { } releaseImageIds)
        {
            foreach (var candidate in releaseImageIds.Where(candidate => !string.Equals(candidate, reference, StringComparison.Ordinal)))
            {
                inspectResult = await TryInspectImageAsync(candidate, cancellationToken).ConfigureAwait(false);
                if (inspectResult is not null)
                {
                    reference = candidate;
                    break;
                }
            }
        }
        if (inspectResult is null)
        {
            throw new InstallerException(
                $"Docker could not inspect the CameraAgent image '{(reference.Length == 0 ? "(unnamed)" : reference)}'.");
        }
        using var inspectJson = JsonDocument.Parse(inspectResult.StandardOutput);
        var image = inspectJson.RootElement[0];
        var imageId = image.GetProperty("Id").GetString() ?? throw new InstallerException("Docker image ID is missing.");
        if (loadedImageIds is not null && !loadedImageIds.Contains(imageId))
        {
            throw new InstallerException("The loaded archive did not contain the requested immutable image.");
        }
        var imageArchitecture = NormalizeArchitecture(image.GetProperty("Architecture").GetString());
        if (image.GetProperty("Os").GetString() != "linux" || imageArchitecture != architecture)
        {
            throw new InstallerException("The CameraAgent image platform does not match the Docker daemon.");
        }

        if (reference.StartsWith("sha256:", StringComparison.Ordinal) &&
            !string.Equals(reference, imageId, StringComparison.Ordinal))
        {
            throw release is null
                ? new InstallerException("The loaded image ID does not match --image-ref.")
                : InstallerException.Refused(
                    $"The CameraAgent image Docker resolved is not the {release.Platform} image release {release.ReleaseTag} names.");
        }

        if (reference.Contains('@', StringComparison.Ordinal))
        {
            var digests = image.GetProperty("RepoDigests").EnumerateArray().Select(static item => item.GetString()).ToArray();
            if (!digests.Contains(reference, StringComparer.Ordinal))
            {
                throw new InstallerException("The pulled image does not expose the requested repository digest.");
            }
        }

        var labels = image.TryGetProperty("Config", out var imageConfig) &&
                     imageConfig.TryGetProperty("Labels", out var imageLabels)
            ? imageLabels
            : default;
        var installationIdentity = CameraAgentImageLabels.Identity(
            request.ImageArchive is null ? "registry" : "archive",
            reference,
            imageId,
            imageArchitecture,
            request.ImageArchiveSha256,
            labels);
        if (release is not null)
        {
            EnsureReleaseImageAgreement(
                release, imageId, installationIdentity.Component, CameraAgentImageLabels.Label(labels, "org.opencontainers.image.version"),
                installationIdentity.SourceRevision);
        }
        return (identity, installationIdentity);
    }

    /// <summary>
    /// Requires the image Docker resolved to be the release's. Its digest pins its bytes, labels included, so the image
    /// must be one the release's offline archive names for this platform, when the release has one, and must name the
    /// release's version and commit, which a release build stamps into it. A mismatch stops the deployment before any
    /// state is touched, and what the step imported is removed, so it is a refusal (exit 3, #1315). The message names only
    /// the release's own validated values, never the image's labels.
    /// </summary>
    private static void EnsureReleaseImageAgreement(
        ReleaseImageSelection release,
        string imageId,
        string? component,
        string? version,
        string? revision)
    {
        if (release.ImageIds is { } identities && !identities.Contains(imageId, StringComparer.Ordinal))
        {
            throw InstallerException.Refused(
                $"The CameraAgent image Docker resolved is not the {release.Platform} image release {release.ReleaseTag} names.");
        }
        if (component != "CameraAgent" || version != release.Version || revision != release.Commit)
        {
            throw InstallerException.Refused(
                $"The CameraAgent image Docker resolved is not labelled as CameraAgent {release.Version} from commit " +
                $"{release.Commit}, as release {release.ReleaseTag} requires.");
        }
    }

    private async Task<ProcessResult?> TryInspectImageAsync(string reference, CancellationToken cancellationToken)
    {
        var result = await RunDockerRawAsync(["image", "inspect", reference], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? result : null;
    }

    private async Task<HashSet<string>> ReadLoadedImageIdsAsync(string output, CancellationToken cancellationToken)
    {
        var references = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static line => line.StartsWith("Loaded image: ", StringComparison.Ordinal)
                ? line["Loaded image: ".Length..]
                : line.StartsWith("Loaded image ID: ", StringComparison.Ordinal)
                    ? line["Loaded image ID: ".Length..]
                    : null)
            .Where(static reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (references.Length == 0)
        {
            throw new InstallerException("Docker did not report an image loaded from the archive.");
        }

        var imageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            var inspect = await RunDockerAsync(["image", "inspect", reference!], cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(inspect.StandardOutput);
            imageIds.Add(document.RootElement[0].GetProperty("Id").GetString()
                ?? throw new InstallerException("A loaded archive image omitted its immutable ID."));
        }
        return imageIds;
    }

    private async Task<DockerDaemonIdentity> ReadDaemonIdentityAsync(CancellationToken cancellationToken)
    {
        var daemonResult = await RunDockerAsync(["info", "--format", "{{json .}}"], cancellationToken).ConfigureAwait(false);
        using var daemonJson = JsonDocument.Parse(daemonResult.StandardOutput);
        return ParseDaemonIdentity(daemonJson.RootElement);
    }

    private static DockerDaemonIdentity ParseDaemonIdentity(JsonElement daemon)
    {
        if (daemon.GetProperty("OSType").GetString() != "linux")
        {
            throw new InstallerException("The Docker daemon must run Linux containers.");
        }

        return new DockerDaemonIdentity(
            daemon.GetProperty("ID").GetString() ?? throw new InstallerException("Docker daemon ID is missing."),
            daemon.GetProperty("Name").GetString() ?? string.Empty,
            NormalizeArchitecture(daemon.GetProperty("Architecture").GetString()),
            daemon.GetProperty("ServerVersion").GetString() ?? string.Empty);
    }

    public Task<ProcessResult> ComposeAsync(
        string composeFile,
        string environmentFile,
        string projectName,
        IReadOnlyList<string> command,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "compose", "--project-name", projectName, "--env-file", environmentFile, "--file", composeFile
        };
        arguments.AddRange(command);
        return RunDockerAsync(arguments, cancellationToken);
    }

    public async Task VerifyContainerOwnershipAsync(
        ComposeFiles compose,
        InstallationPaths paths,
        ImageInstallationIdentity imageIdentity,
        uint uid,
        uint gid,
        CancellationToken cancellationToken)
    {
        var result = await RunDockerAsync(["container", "inspect", compose.ContainerName], cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var container = document.RootElement[0];
        var config = container.GetProperty("Config");
        var host = container.GetProperty("HostConfig");
        var mounts = container.GetProperty("Mounts").EnumerateArray().ToArray();
        var labels = config.GetProperty("Labels");
        var expectedInstance = paths.InstanceRoot.Split(Path.DirectorySeparatorChar).Last();
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["image"] = container.GetProperty("Image").GetString() == imageIdentity.ImageId,
            ["user"] = config.GetProperty("User").GetString() == $"{uid}:{gid}",
            ["project"] = labels.GetProperty("com.docker.compose.project").GetString() == compose.ProjectName,
            ["ownership"] = labels.TryGetProperty("io.hvo.skymonitor.instance-id", out var instanceLabel) &&
                            instanceLabel.GetString() == expectedInstance,
            ["read-only-root"] = host.GetProperty("ReadonlyRootfs").GetBoolean(),
            ["unprivileged"] = !host.GetProperty("Privileged").GetBoolean(),
            ["configuration-mount"] = mounts.Any(mount => MountMatches(
                mount, Path.Combine(paths.ConfigRoot, "camera-module.json"), "/app/cameraagent.deploy.json", writable: false)),
            ["catalog-mount"] = mounts.Any(mount => MountMatches(mount, paths.CatalogRoot, "/app/catalog", writable: false)),
            ["identity-mount"] = mounts.Any(mount => MountMatches(
                mount, Path.Combine(paths.StateRoot, "identity"), "/app/App_Data", writable: true))
        };
        if (compose.ReplayRunnerContainerName is not null)
        {
            checks["replay-socket-mount"] = mounts.Any(mount => MountMatches(
                mount, Path.Combine(paths.StateRoot, "replay-runner"), "/run/hvo-replay", writable: true));
        }
        var failed = checks.Where(static check => !check.Value).Select(static check => check.Key).ToArray();
        if (failed.Length > 0)
            throw new InstallerException(
                $"The selected CameraAgent container does not match the installation manifest: {string.Join(", ", failed)}.");
    }

    public async Task VerifyRecoveryContainerAsync(
        ComposeFiles compose, InstallationPaths paths, InstanceManifest manifest, CancellationToken cancellationToken)
    {
        var result = await RunDockerAsync(["container", "inspect", compose.ContainerName], cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var container = document.RootElement[0];
        var host = container.GetProperty("HostConfig");
        var mounts = container.GetProperty("Mounts").EnumerateArray().ToArray();
        var ports = host.GetProperty("PortBindings").EnumerateObject().ToArray();
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["capabilities-dropped"] = host.GetProperty("CapDrop").EnumerateArray().Any(static item => item.GetString() == "ALL"),
            ["no-added-capabilities"] = !host.TryGetProperty("CapAdd", out var added) || added.ValueKind == JsonValueKind.Null || added.GetArrayLength() == 0,
            ["no-new-privileges"] = host.GetProperty("SecurityOpt").EnumerateArray().Any(static item => item.GetString() == "no-new-privileges:true"),
            ["ports"] = ports.Length == 1 && ports[0].Name == "8080/tcp" && ports[0].Value.GetArrayLength() == 1 &&
                        ports[0].Value[0].GetProperty("HostIp").GetString() == manifest.BindAddress &&
                        ports[0].Value[0].GetProperty("HostPort").GetString() == manifest.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["secrets-mount"] = mounts.Any(mount => MountMatches(mount, Path.Combine(paths.ConfigRoot, "secrets"), "/run/hvo-secrets", writable: false))
        };
        foreach (var source in CameraAgentStateLayout.WritableBindSources(paths.StateRoot, manifest.ReplayProfile))
            checks[source.ContainerPath] = mounts.Any(mount => MountMatches(mount, source.HostPath, source.ContainerPath, writable: true));
        var failed = checks.Where(static check => !check.Value).Select(static check => check.Key).ToArray();
        if (failed.Length > 0)
            throw new InstallerException($"Restore-only runtime authority differs from the original instance: {string.Join(", ", failed)}.");
    }

    public async Task VerifyContainerAsync(
        ComposeFiles compose,
        InstallationPaths paths,
        ImageInstallationIdentity imageIdentity,
        uint uid,
        uint gid,
        CancellationToken cancellationToken)
    {
        var expectedCatalog = paths.CatalogRoot;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (true)
        {
            var result = await RunDockerAsync(
                ["container", "inspect", compose.ContainerName], cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var container = document.RootElement[0];
            var state = container.GetProperty("State");
            var config = container.GetProperty("Config");
            var host = container.GetProperty("HostConfig");
            var mounts = container.GetProperty("Mounts").EnumerateArray().ToArray();
            var checks = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["running"] = state.GetProperty("Running").GetBoolean(),
                ["healthy"] = state.GetProperty("Health").GetProperty("Status").GetString() == "healthy",
                ["image"] = container.GetProperty("Image").GetString() == imageIdentity.ImageId,
                ["user"] = config.GetProperty("User").GetString() == $"{uid}:{gid}",
                ["project"] = config.GetProperty("Labels").GetProperty("com.docker.compose.project").GetString() == compose.ProjectName,
                ["ownership"] = config.GetProperty("Labels").TryGetProperty("io.hvo.skymonitor.instance-id", out var instanceLabel) &&
                                instanceLabel.GetString() == paths.InstanceRoot.Split(Path.DirectorySeparatorChar).Last(),
                ["read-only-root"] = host.GetProperty("ReadonlyRootfs").GetBoolean(),
                ["unprivileged"] = !host.GetProperty("Privileged").GetBoolean(),
                ["configuration-mount"] = mounts.Any(mount => MountMatches(
                    mount, Path.Combine(paths.ConfigRoot, "camera-module.json"), "/app/cameraagent.deploy.json", writable: false)),
                ["catalog-mount"] = mounts.Any(mount => MountMatches(mount, expectedCatalog, "/app/catalog", writable: false)),
                ["identity-mount"] = mounts.Any(mount => MountMatches(
                    mount, Path.Combine(paths.StateRoot, "identity"), "/app/App_Data", writable: true))
            };
            if (compose.ReplayRunnerContainerName is not null)
            {
                checks["replay-socket-mount"] = mounts.Any(mount => MountMatches(
                    mount, Path.Combine(paths.StateRoot, "replay-runner"), "/run/hvo-replay", writable: true));
            }
            var failed = checks.Where(static check => !check.Value).Select(static check => check.Key).ToArray();
            if (failed.Length == 0)
            {
                if (compose.ReplayRunnerContainerName is not null)
                {
                    await VerifyReplayRunnerAsync(
                        compose,
                        paths,
                        imageIdentity,
                        uid,
                        gid,
                        cancellationToken).ConfigureAwait(false);
                }
                return;
            }
            if (failed is ["healthy"] && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                continue;
            }
            throw new InstallerException(
                $"The running CameraAgent container does not match the installation manifest: {string.Join(", ", failed)}.");
        }
    }

    private async Task VerifyReplayRunnerAsync(
        ComposeFiles compose,
        InstallationPaths paths,
        ImageInstallationIdentity imageIdentity,
        uint uid,
        uint gid,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (true)
        {
            var result = await RunDockerAsync(
                ["container", "inspect", compose.ReplayRunnerContainerName!],
                cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var container = document.RootElement[0];
            var state = container.GetProperty("State");
            var config = container.GetProperty("Config");
            var host = container.GetProperty("HostConfig");
            var mounts = container.GetProperty("Mounts").EnumerateArray().ToArray();
            var restrictedMounts = mounts.Where(static mount => !IsReplayRunnerTmpfs(mount)).ToArray();
            var expectedInstance = paths.InstanceRoot.Split(Path.DirectorySeparatorChar).Last();
            var checks = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["runner-running"] = state.GetProperty("Running").GetBoolean(),
                ["runner-healthy"] = state.TryGetProperty("Health", out var health) &&
                                     health.GetProperty("Status").GetString() == "healthy",
                ["runner-image"] = container.GetProperty("Image").GetString() == imageIdentity.ImageId,
                ["runner-user"] = config.GetProperty("User").GetString() == $"{uid}:{gid}",
                ["runner-entrypoint"] = config.GetProperty("Entrypoint").EnumerateArray().Select(static item => item.GetString())
                    .SequenceEqual(["/app/replay-runner/HVO.SkyMonitor.CameraAgent.ReplayRunner"], StringComparer.Ordinal),
                ["runner-project"] = config.GetProperty("Labels").GetProperty("com.docker.compose.project").GetString() == compose.ProjectName,
                ["runner-ownership"] = config.GetProperty("Labels").TryGetProperty(
                    "io.hvo.skymonitor.instance-id",
                    out var instanceLabel) && instanceLabel.GetString() == expectedInstance,
                ["runner-read-only-root"] = host.GetProperty("ReadonlyRootfs").GetBoolean(),
                ["runner-unprivileged"] = !host.GetProperty("Privileged").GetBoolean(),
                ["runner-capabilities-dropped"] = host.GetProperty("CapDrop").EnumerateArray()
                    .Any(static item => item.GetString() == "ALL"),
                ["runner-no-new-privileges"] = host.GetProperty("SecurityOpt").EnumerateArray()
                    .Any(static item => item.GetString() == "no-new-privileges:true"),
                ["runner-network-disabled"] = host.GetProperty("NetworkMode").GetString() == "none",
                ["runner-tmpfs"] = ReplayRunnerTmpfsMatches(host),
                ["runner-auth-key-only"] = restrictedMounts.Length == 2 && restrictedMounts.Any(mount => MountMatches(
                    mount,
                    Path.Combine(paths.ConfigRoot, "secrets", "replay-runner-auth-key"),
                    "/run/hvo-secrets/replay-runner-auth-key",
                    writable: false)),
                ["runner-socket-mount"] = restrictedMounts.Any(mount => MountMatches(
                    mount,
                    Path.Combine(paths.StateRoot, "replay-runner"),
                    "/run/hvo-replay",
                    writable: true))
            };
            var failed = checks.Where(static check => !check.Value).Select(static check => check.Key).ToArray();
            if (failed.Length == 0)
            {
                return;
            }
            if (failed.All(static check => check is "runner-running" or "runner-healthy") &&
                DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                continue;
            }
            throw new InstallerException(
                $"The local replay runner does not match the installation manifest: {string.Join(", ", failed)}.");
        }
    }

    public async Task<ContainerRuntimeIdentity> InspectContainerAsync(
        string containerName,
        CancellationToken cancellationToken)
    {
        var result = await RunDockerRawAsync(["container", "inspect", containerName], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var diagnostic = result.StandardError.Trim();
            if (diagnostic.Equals($"Error response from daemon: No such container: {containerName}", StringComparison.OrdinalIgnoreCase) ||
                diagnostic.Equals($"Error response from daemon: No such object: {containerName}", StringComparison.OrdinalIgnoreCase))
            {
                return new ContainerRuntimeIdentity(false, false, false, null);
            }
            throw new InstallerException($"Docker could not authenticate container '{containerName}': {Redaction.SafeDiagnostic(result.StandardError)}");
        }
        using var document = JsonDocument.Parse(result.StandardOutput);
        var container = document.RootElement[0];
        var state = container.GetProperty("State");
        var healthy = state.TryGetProperty("Health", out var health) &&
                      health.GetProperty("Status").GetString() == "healthy";
        return new ContainerRuntimeIdentity(
            true,
            state.GetProperty("Running").GetBoolean(),
            healthy,
            container.GetProperty("Image").GetString());
    }

    public async Task EnsureNoInstanceReferencesAsync(
        Guid instanceId,
        string instanceRoot,
        CancellationToken cancellationToken,
        IReadOnlyList<DestructiveTreeNode>? tree = null)
    {
        var listed = await RunDockerAsync(["container", "ls", "--all", "--quiet"], cancellationToken).ConfigureAwait(false);
        foreach (var id in listed.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var inspected = await RunDockerAsync(["container", "inspect", id], cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(inspected.StandardOutput);
            var container = document.RootElement[0];
            var labels = container.GetProperty("Config").GetProperty("Labels");
            var ownsInstance = labels.ValueKind == JsonValueKind.Object &&
                               labels.TryGetProperty("io.hvo.skymonitor.instance-id", out var label) &&
                               label.GetString() == instanceId.ToString("D");
            var usesRoot = container.GetProperty("Mounts").EnumerateArray().Any(mount =>
            {
                var source = mount.GetProperty("Source").GetString();
                return source == instanceRoot ||
                       source?.StartsWith(instanceRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) == true ||
                       source is not null && (instanceRoot.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                                              ReferencesTreeNode(source, tree));
            });
            if (ownsInstance || usesRoot)
            {
                throw new InstallerException("Purge refused because a local container still references the instance.");
            }
        }
    }

    public async Task EnsureNoPathReferencesAsync(
        string path,
        CancellationToken cancellationToken,
        IReadOnlyList<DestructiveTreeNode>? tree = null)
    {
        var pathIdentities = ExistingAncestorIdentities(path);
        var listed = await RunDockerAsync(["container", "ls", "--all", "--quiet"], cancellationToken).ConfigureAwait(false);
        foreach (var id in listed.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var inspected = await RunDockerAsync(["container", "inspect", id], cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(inspected.StandardOutput);
            if (document.RootElement[0].GetProperty("Mounts").EnumerateArray().Any(mount =>
                {
                    var source = mount.GetProperty("Source").GetString();
                    return source == path || source?.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.Ordinal) == true ||
                           source is not null && (path.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                                                  ReferencesSameNode(source, pathIdentities) || ReferencesTreeNode(source, tree));
                }))
            {
                throw new InstallerException("Catalog garbage collection refused a path referenced by a local container.");
            }
        }
    }

    private static List<UnixNodeIdentity> ExistingAncestorIdentities(string path)
    {
        var identities = new List<UnixNodeIdentity>();
        for (var current = path; current is not null && current != Path.GetPathRoot(current); current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current) || File.Exists(current)) identities.Add(NativeLinux.GetNodeIdentity(current));
        }
        return identities;
    }

    private static bool ReferencesSameNode(string source, IReadOnlyList<UnixNodeIdentity> pathIdentities)
    {
        if (!Directory.Exists(source) && !File.Exists(source)) return false;
        var sourceIdentity = NativeLinux.GetTargetNodeIdentity(source);
        return pathIdentities.Any(identity => identity.DeviceMajor == sourceIdentity.DeviceMajor &&
                                              identity.DeviceMinor == sourceIdentity.DeviceMinor &&
                                              identity.Inode == sourceIdentity.Inode);
    }

    private static bool ReferencesTreeNode(string source, IReadOnlyList<DestructiveTreeNode>? tree)
    {
        if (tree is null || (!Directory.Exists(source) && !File.Exists(source))) return false;
        var sourceIdentity = NativeLinux.GetTargetNodeIdentity(source);
        return tree.Any(node => node.DeviceMajor == sourceIdentity.DeviceMajor && node.DeviceMinor == sourceIdentity.DeviceMinor &&
                                node.Inode == sourceIdentity.Inode);
    }

    public async Task<string> ReadContainerLogsAsync(string containerName, CancellationToken cancellationToken)
    {
        var result = await RunDockerRawAsync(
            ["container", "logs", "--tail", "200", containerName], cancellationToken).ConfigureAwait(false);
        return Redaction.SafeDiagnostics(string.Concat(result.StandardOutput, "\n", result.StandardError));
    }

    private static bool MountMatches(JsonElement mount, string source, string destination, bool writable)
        => mount.GetProperty("Source").GetString() == source &&
           mount.GetProperty("Destination").GetString() == destination &&
           mount.GetProperty("RW").GetBoolean() == writable;

    private static bool IsReplayRunnerTmpfs(JsonElement mount) =>
        mount.TryGetProperty("Type", out var type) && type.GetString() == "tmpfs" &&
        mount.TryGetProperty("Destination", out var destination) && destination.GetString() == "/tmp";

    private static bool ReplayRunnerTmpfsMatches(JsonElement host)
    {
        if (!host.TryGetProperty("Tmpfs", out var tmpfs) || tmpfs.ValueKind != JsonValueKind.Object ||
            tmpfs.EnumerateObject().Count() != 1 || !tmpfs.TryGetProperty("/tmp", out var value))
        {
            return false;
        }

        var options = (value.GetString() ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return options.Length == 6 &&
               options.Count(static option => option.Equals("rw", StringComparison.OrdinalIgnoreCase)) == 1 &&
               options.Count(static option => option.Equals("nosuid", StringComparison.OrdinalIgnoreCase)) == 1 &&
               options.Count(static option => option.Equals("nodev", StringComparison.OrdinalIgnoreCase)) == 1 &&
               options.Count(static option => option.Equals("noexec", StringComparison.OrdinalIgnoreCase)) == 1 &&
               options.Count(static option => option.Equals("mode=1777", StringComparison.OrdinalIgnoreCase)) == 1 &&
               options.Count(static option => option.Equals("size=64m", StringComparison.OrdinalIgnoreCase) ||
                                              option.Equals("size=65536k", StringComparison.OrdinalIgnoreCase) ||
                                              option.Equals("size=67108864", StringComparison.OrdinalIgnoreCase)) == 1;
    }


    private async Task<ProcessResult> RunDockerAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunDockerRawAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InstallerException($"Docker command failed ({string.Join(' ', arguments.Take(2))}): {Redaction.SafeDiagnostic(result.StandardError)}");
        }
        return result;
    }

    private async Task<ProcessResult> RunDockerRawAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (endpoint is null)
        {
            var context = await processRunner.RunAsync(
                "docker", ["context", "inspect", "--format", "{{.Endpoints.docker.Host}}"], cancellationToken).ConfigureAwait(false);
            if (context.ExitCode != 0)
                throw new InstallerException($"Docker endpoint discovery failed: {Redaction.SafeDiagnostic(context.StandardError)}");
            endpoint = context.StandardOutput.Trim();
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) || endpointUri.Scheme != "unix")
                throw new InstallerException("Local lifecycle operations require an authenticated Unix-socket Docker endpoint.");
        }
        var pinnedArguments = new List<string>(arguments.Count + 2) { "--host", endpoint };
        pinnedArguments.AddRange(arguments);
        return await processRunner.RunAsync("docker", pinnedArguments, cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeArchitecture(string? value) => value switch
    {
        "amd64" or "x86_64" => "amd64",
        "arm64" or "aarch64" => "arm64",
        _ => throw new InstallerException($"Unsupported Docker architecture '{value}'.")
    };
}
