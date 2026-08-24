using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal sealed class DockerClient(IProcessRunner processRunner)
{
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

        return (identity, new ImageInstallationIdentity(
            request.ImageArchive is null ? "registry" : "archive",
            request.ImageReference,
            imageId,
            imageArchitecture,
            request.ImageArchiveSha256));
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
                ["read-only-root"] = host.GetProperty("ReadonlyRootfs").GetBoolean(),
                ["unprivileged"] = !host.GetProperty("Privileged").GetBoolean(),
                ["configuration-mount"] = mounts.Any(mount => MountMatches(
                    mount, Path.Combine(paths.ConfigRoot, "camera-module.json"), "/app/cameraagent.deploy.json", writable: false)),
                ["catalog-mount"] = mounts.Any(mount => MountMatches(mount, expectedCatalog, "/app/catalog", writable: false)),
                ["identity-mount"] = mounts.Any(mount => MountMatches(
                    mount, Path.Combine(paths.StateRoot, "identity"), "/app/App_Data", writable: true))
            };
            var failed = checks.Where(static check => !check.Value).Select(static check => check.Key).ToArray();
            if (failed.Length == 0)
            {
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

    private static bool MountMatches(JsonElement mount, string source, string destination, bool writable)
        => mount.GetProperty("Source").GetString() == source &&
           mount.GetProperty("Destination").GetString() == destination &&
           mount.GetProperty("RW").GetBoolean() == writable;

    private async Task<ProcessResult> RunDockerAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync("docker", arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InstallerException($"Docker command failed ({string.Join(' ', arguments.Take(2))}): {Redaction.SafeDiagnostic(result.StandardError)}");
        }
        return result;
    }

    private static string NormalizeArchitecture(string? value) => value switch
    {
        "amd64" or "x86_64" => "amd64",
        "arm64" or "aarch64" => "arm64",
        _ => throw new InstallerException($"Unsupported Docker architecture '{value}'.")
    };
}
