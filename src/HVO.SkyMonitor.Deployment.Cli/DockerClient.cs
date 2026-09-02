using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

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

    public async Task<(DockerDaemonIdentity Daemon, ImageInstallationIdentity Image)> PrepareImageAsync(
        InstallRequest request,
        bool allowMutation,
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

        var inspectResult = await RunDockerAsync(["image", "inspect", request.ImageReference], cancellationToken).ConfigureAwait(false);
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

        if (request.ImageReference.StartsWith("sha256:", StringComparison.Ordinal) &&
            !string.Equals(request.ImageReference, imageId, StringComparison.Ordinal))
        {
            throw new InstallerException("The loaded image ID does not match --image-ref.");
        }

        if (request.ImageReference.Contains('@', StringComparison.Ordinal))
        {
            var digests = image.GetProperty("RepoDigests").EnumerateArray().Select(static item => item.GetString()).ToArray();
            if (!digests.Contains(request.ImageReference, StringComparer.Ordinal))
            {
                throw new InstallerException("The pulled image does not expose the requested repository digest.");
            }
        }

        var labels = image.TryGetProperty("Config", out var imageConfig) &&
                     imageConfig.TryGetProperty("Labels", out var imageLabels)
            ? imageLabels
            : default;
        var upgradeCompatibility = labels.ValueKind == JsonValueKind.Object &&
                                   labels.TryGetProperty("io.hvo.skymonitor.state-compatibility", out var compatibility)
            ? compatibility.GetString()
            : null;
        var sourceRevision = labels.ValueKind == JsonValueKind.Object &&
                             labels.TryGetProperty("org.opencontainers.image.revision", out var revision)
            ? revision.GetString()
            : null;
        var component = Label(labels, "io.hvo.skymonitor.component");
        var configurationContract = Label(labels, "io.hvo.skymonitor.configuration-contract");
        var catalogContract = Label(labels, "io.hvo.skymonitor.catalog-contract");
        var replayRunnerContract = Label(labels, "io.hvo.skymonitor.replay-runner-contract");

        return (identity, new ImageInstallationIdentity(
            request.ImageArchive is null ? "registry" : "archive",
            request.ImageReference,
            imageId,
            imageArchitecture,
            request.ImageArchiveSha256,
            UpgradeCompatibility: upgradeCompatibility,
            SourceRevision: sourceRevision,
            Component: component,
            ConfigurationContract: configurationContract,
            CatalogContract: catalogContract,
            ReplayRunnerContract: replayRunnerContract));
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
        var daemon = daemonJson.RootElement;
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
                ["runner-auth-key-only"] = mounts.Length == 2 && mounts.Any(mount => MountMatches(
                    mount,
                    Path.Combine(paths.ConfigRoot, "secrets", "replay-runner-auth-key"),
                    "/run/hvo-secrets/replay-runner-auth-key",
                    writable: false)),
                ["runner-socket-mount"] = mounts.Any(mount => MountMatches(
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
        return Redaction.SafeDiagnostic(string.Concat(result.StandardOutput, "\n", result.StandardError));
    }

    private static bool MountMatches(JsonElement mount, string source, string destination, bool writable)
        => mount.GetProperty("Source").GetString() == source &&
           mount.GetProperty("Destination").GetString() == destination &&
           mount.GetProperty("RW").GetBoolean() == writable;

    private static string? Label(JsonElement labels, string name)
        => labels.ValueKind == JsonValueKind.Object && labels.TryGetProperty(name, out var value)
            ? value.GetString()
            : null;

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
