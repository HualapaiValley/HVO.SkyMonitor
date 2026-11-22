using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace HVO.SkyMonitor.Deployment;

/// <summary>The pinned stock Silo image as the local daemon holds it (#1966).</summary>
internal sealed record SiloImageIdentity(string Reference, string ImageId, string Architecture, string PlatformDigest, string Release, string Revision);

/// <summary>What the read-only Silo container check observed (#1966; contract sections 2 and 7.2).</summary>
internal sealed record SiloContainerObservation(
    bool Running,
    bool Healthy,
    string? ImageId,
    string? User,
    bool ReadOnlyRootFileSystem,
    bool PublishesPorts,
    IReadOnlyList<string> Networks,
    bool PrivateNetworkInternal,
    string? DataRootSource,
    bool ConsoleDisabled,
    bool DiskSyncOnWrite,
    int? ProcessId)
{
    /// <summary>The container's full ID.</summary>
    public string? ContainerId { get; init; }

    /// <summary>
    /// The platform manifest the container actually runs, as Docker's containerd image store reports it; absent under
    /// the classic store, where the image ID (the config digest) fixes the platform instead.
    /// </summary>
    public string? ImageManifestDigest { get; init; }

    public string? ImageManifestArchitecture { get; init; }

    public string? ImageManifestOperatingSystem { get; init; }

    /// <summary>The aliases the container answers to on each network it is attached to.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> NetworkAliases { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}

/// <summary>A Docker network as <c>docker network inspect</c> reports it: identity, labels and its running members.</summary>
internal sealed record DockerNetworkObservation(
    string Name,
    string Id,
    string? Driver,
    bool Internal,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlySet<string> ContainerIds);

/// <summary>The CameraAgent container of a Silo-layout instance as a verification sees it now, not as a result recorded it.</summary>
internal sealed record ApplicationContainerObservation(
    string Id,
    bool Running,
    string? Health,
    string? ImageId,
    DateTimeOffset? StartedUtc,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<string> Networks)
{
    /// <summary>The container's mounts as Docker reports them: type, host source, destination and whether writable.</summary>
    public IReadOnlyList<(string? Type, string? Source, string? Destination, bool Writable)> Mounts { get; init; } = [];

    /// <summary>The container's environment, which can override a setting its mounted setting files give.</summary>
    public IReadOnlyList<string> Environment { get; init; } = [];
}

/// <summary>The pinned Silo image as this daemon holds it for its own platform: the reference to run it by and its inspection.</summary>
internal sealed record ResolvedSiloImage(string Reference, string Inspection);

internal sealed partial class DockerClient
{
    /// <summary>
    /// Finds the pinned Silo image for this daemon's own platform and proves it is that release (#1966): already present
    /// as exactly this platform's child of the pin (<see cref="ResolvePinnedSiloImageAsync"/>); otherwise loaded from the
    /// operator-staged archive's verified snapshot, so Docker reads exactly the bytes that were verified whatever happens
    /// to the staged path; otherwise pulled by the index digest for this platform when the run may download. A cached
    /// image of another platform is not present and never stands in for, or suppresses, the verified archive. Its OS,
    /// architecture and release, revision and licence labels must be the pin's. A plan never loads or pulls.
    /// </summary>
    public async Task<SiloImageIdentity> PrepareSiloImageAsync(
        string daemonArchitecture, bool allowPull, SiloImageSnapshot? archive, CancellationToken cancellationToken)
    {
        var platform = SiloImagePin.ForArchitecture(daemonArchitecture)
            ?? throw InstallerException.Refused($"The pinned Silo release has no image for the {daemonArchitecture} architecture.");
        var present = await ResolvePinnedSiloImageAsync(platform, cancellationToken).ConfigureAwait(false);
        if (present is null && archive is not null)
        {
            _ = await RunDockerAsync(["image", "load", "--input", archive.DescriptorPath], cancellationToken).ConfigureAwait(false);
            present = await ResolvePinnedSiloImageAsync(platform, cancellationToken).ConfigureAwait(false);
        }
        if (present is null && allowPull)
        {
            await RunDockerAsync(["image", "pull", "--quiet", "--platform", platform.Platform, SiloImagePin.Reference], cancellationToken)
                .ConfigureAwait(false);
            present = await ResolvePinnedSiloImageAsync(platform, cancellationToken).ConfigureAwait(false);
        }
        if (present is null)
            throw InstallerException.Refused(
                $"The pinned Silo image for {platform.Platform} is not on this Docker daemon as that platform's own image; pull it " +
                "online, or stage it with --silo-image-archive. Docker's containerd image store needs Docker Engine 28 or later to " +
                "resolve the pinned image's platform.");
        using var document = JsonDocument.Parse(present.Inspection);
        var image = document.RootElement[0];
        var labels = image.GetProperty("Config").TryGetProperty("Labels", out var labelValue) && labelValue.ValueKind == JsonValueKind.Object
            ? labelValue
            : default;
        string? Label(string name) => labels.ValueKind == JsonValueKind.Object && labels.TryGetProperty(name, out var value)
            ? value.GetString()
            : null;
        if ((image.TryGetProperty("Os", out var os) ? os.GetString() : null) != "linux" ||
            (image.TryGetProperty("Architecture", out var arch) ? arch.GetString() : null) != platform.Architecture ||
            Label("org.opencontainers.image.version") != SiloImagePin.Release ||
            Label("org.opencontainers.image.revision") != SiloImagePin.SourceRevision ||
            Label("org.opencontainers.image.licenses") != SiloImagePin.License)
        {
            throw InstallerException.Refused("The local Silo image is not the pinned release for this architecture.");
        }
        return new SiloImageIdentity(SiloImagePin.Reference, present.Reference, platform.Architecture, platform.ManifestDigest,
            SiloImagePin.Release, SiloImagePin.SourceRevision);
    }

    /// <summary>
    /// The pinned Silo image as this daemon holds it for <paramref name="platform"/> exactly, or null. Read-only. Docker's
    /// classic store names it by its config digest, which fixes the platform; the containerd store names a loaded
    /// single-platform image by its platform manifest digest, and a pulled one by the pinned index, whose child for this
    /// platform only <c>image inspect --platform</c> resolves. An image whose resolved manifest or config is another
    /// platform's is not the pinned image here. A missing image is null; any other daemon failure fails closed.
    /// </summary>
    public async Task<ResolvedSiloImage?> ResolvePinnedSiloImageAsync(SiloImagePin.PlatformImage platform, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(platform);
        var classic = await InspectSiloImageAsync(["image", "inspect", platform.ConfigDigest], cancellationToken).ConfigureAwait(false);
        if (classic is not null && Resolves(classic, platform, platform.ConfigDigest, descriptor: null))
            return new ResolvedSiloImage(platform.ConfigDigest, classic);
        var loaded = await InspectSiloImageAsync(["image", "inspect", platform.ManifestDigest], cancellationToken).ConfigureAwait(false);
        if (loaded is not null && Resolves(loaded, platform, platform.ManifestDigest, platform.ManifestDigest))
            return new ResolvedSiloImage(platform.ManifestDigest, loaded);
        var pulled = await InspectSiloImageAsync(["image", "inspect", "--platform", platform.Platform, SiloImagePin.IndexDigest], cancellationToken)
            .ConfigureAwait(false);
        if (pulled is not null && Resolves(pulled, platform, platform.ManifestDigest, platform.ManifestDigest))
            return new ResolvedSiloImage(SiloImagePin.IndexDigest, pulled);
        return null;
    }

    private async Task<string?> InspectSiloImageAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var inspected = await RunDockerRawAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (inspected.ExitCode == 0) return inspected.StandardOutput;
        var diagnostic = inspected.StandardError;
        // Absent, or (for --platform) a CLI or daemon too old to resolve a platform: not resolvable this way.
        if (diagnostic.Contains("No such image", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("No such object", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("unknown flag", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("requires API version", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("does not provide the specified platform", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("does not match the specified platform", StringComparison.OrdinalIgnoreCase))
            return null;
        throw new InstallerException($"Docker could not inspect the Silo image: {Redaction.SafeDiagnostic(diagnostic)}");
    }

    /// <summary>Whether an image inspection is exactly the pinned image for <paramref name="platform"/> under <paramref name="id"/>.</summary>
    private static bool Resolves(string inspection, SiloImagePin.PlatformImage platform, string id, string? descriptor)
    {
        using var document = JsonDocument.Parse(inspection);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != 1) return false;
        var image = document.RootElement[0];
        string? Text(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        var observedDescriptor = image.TryGetProperty("Descriptor", out var target) ? Text(target, "digest") : null;
        return Text(image, "Id") == id && Text(image, "Os") == "linux" && Text(image, "Architecture") == platform.Architecture &&
               (descriptor is null ? observedDescriptor is null || observedDescriptor == platform.ConfigDigest : observedDescriptor == descriptor);
    }

    /// <summary>Whether the daemon holds <paramref name="reference"/>, and the value of one of its labels. Read-only.</summary>
    public async Task<(bool Present, string? Value)> TryReadImageLabelAsync(string reference, string name, CancellationToken cancellationToken)
    {
        var inspected = await TryInspectImageAsync(reference, cancellationToken).ConfigureAwait(false);
        if (inspected is null) return (false, null);
        using var document = JsonDocument.Parse(inspected.StandardOutput);
        var value = document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() == 1 &&
                    document.RootElement[0].TryGetProperty("Config", out var config) &&
                    config.ValueKind == JsonValueKind.Object && config.TryGetProperty("Labels", out var labels)
            ? CameraAgentImageLabels.Label(labels, name)
            : null;
        return (true, value);
    }

    /// <summary>Runs a bootstrap or verification script in the instance's Silo container; the script travels on standard input only.</summary>
    public async Task<ProcessResult> ExecSiloScriptAsync(string containerName, string script, CancellationToken cancellationToken)
    {
        if (processRunner is not IProcessInputRunner inputRunner)
            throw new InstallerException("Silo bootstrap requires a process runner that supplies standard input.");
        // Make sure the endpoint is pinned before the script runs.
        _ = await RunDockerAsync(["version", "--format", "{{.Server.Version}}"], cancellationToken).ConfigureAwait(false);
        return await inputRunner.RunWithInputAsync(
                "docker",
                ["--host", endpoint!, "exec", "--interactive", containerName, "bash", "-s"],
                script + "\n",
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Waits until the Silo container reports healthy, failing on exit or after <paramref name="timeout"/>.</summary>
    public async Task WaitForSiloHealthyAsync(string containerName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var observed = await ObserveSiloContainerAsync(containerName, cancellationToken).ConfigureAwait(false);
            if (observed?.Healthy == true) return;
            if (observed is { Running: false })
                throw new InstallerException("The Silo container stopped before it became healthy.");
            if (stopwatch.Elapsed > timeout)
                throw new InstallerException("The Silo container did not become healthy within its budget.");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the Silo container's runtime shape, or null when it does not exist. Changes nothing.</summary>
    public async Task<SiloContainerObservation?> ObserveSiloContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        var inspected = await RunDockerRawAsync(["container", "inspect", containerName], cancellationToken).ConfigureAwait(false);
        if (inspected.ExitCode != 0) return null;
        using var document = JsonDocument.Parse(inspected.StandardOutput);
        var container = document.RootElement[0];
        var state = container.GetProperty("State");
        var host = container.GetProperty("HostConfig");
        var config = container.GetProperty("Config");
        var environment = config.TryGetProperty("Env", out var env) && env.ValueKind == JsonValueKind.Array
            ? env.EnumerateArray().Select(static item => item.GetString()).OfType<string>().ToArray()
            : [];
        var publishes = host.TryGetProperty("PortBindings", out var bindings) && bindings.ValueKind == JsonValueKind.Object &&
                        bindings.EnumerateObject().Any(static binding => binding.Value.ValueKind == JsonValueKind.Array &&
                                                                         binding.Value.GetArrayLength() > 0);
        publishes |= host.TryGetProperty("PublishAllPorts", out var all) && all.ValueKind == JsonValueKind.True;
        var networks = container.GetProperty("NetworkSettings").TryGetProperty("Networks", out var networkValue) &&
                       networkValue.ValueKind == JsonValueKind.Object
            ? networkValue.EnumerateObject().Select(static network => network.Name).ToArray()
            : [];
        var aliases = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (networkValue.ValueKind == JsonValueKind.Object)
        {
            foreach (var network in networkValue.EnumerateObject())
                aliases[network.Name] = network.Value.ValueKind == JsonValueKind.Object && network.Value.TryGetProperty("Aliases", out var names) &&
                                        names.ValueKind == JsonValueKind.Array
                    ? [.. names.EnumerateArray().Select(static name => name.GetString()).OfType<string>()]
                    : [];
        }
        var manifest = container.TryGetProperty("ImageManifestDescriptor", out var descriptor) && descriptor.ValueKind == JsonValueKind.Object
            ? descriptor
            : default;
        var manifestPlatform = manifest.ValueKind == JsonValueKind.Object && manifest.TryGetProperty("platform", out var platformValue) &&
                               platformValue.ValueKind == JsonValueKind.Object
            ? platformValue
            : default;
        string? Text(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        var internalNetwork = networks.Length == 1 &&
                              await IsInternalNetworkAsync(networks[0], cancellationToken).ConfigureAwait(false);
        string? dataSource = null;
        foreach (var mount in container.GetProperty("Mounts").EnumerateArray())
        {
            if (mount.GetProperty("Destination").GetString() == "/data")
                dataSource = mount.GetProperty("Source").GetString();
        }
        var health = state.TryGetProperty("Health", out var healthValue) && healthValue.ValueKind == JsonValueKind.Object
            ? healthValue.GetProperty("Status").GetString()
            : null;
        var pid = state.TryGetProperty("Pid", out var pidValue) && pidValue.TryGetInt32(out var number) && number > 0 ? number : (int?)null;
        return new SiloContainerObservation(
            state.GetProperty("Running").GetBoolean(),
            health == "healthy",
            container.GetProperty("Image").GetString(),
            config.TryGetProperty("User", out var user) ? user.GetString() : null,
            host.TryGetProperty("ReadonlyRootfs", out var readOnly) && readOnly.ValueKind == JsonValueKind.True,
            publishes,
            networks,
            internalNetwork,
            dataSource,
            environment.Contains("MINIO_BROWSER=off", StringComparer.Ordinal),
            environment.Contains("MINIO_FS_OSYNC=on", StringComparer.Ordinal),
            pid)
        {
            ContainerId = Text(container, "Id"),
            ImageManifestDigest = Text(manifest, "digest"),
            ImageManifestArchitecture = Text(manifestPlatform, "architecture"),
            ImageManifestOperatingSystem = Text(manifestPlatform, "os"),
            NetworkAliases = aliases
        };
    }

    /// <summary>
    /// A network's identity, labels and running members, or null when it does not exist. Read-only; any other daemon
    /// failure fails closed rather than reading as an absent network.
    /// </summary>
    public async Task<DockerNetworkObservation?> InspectNetworkAsync(string network, CancellationToken cancellationToken)
    {
        var inspected = await RunDockerRawAsync(["network", "inspect", network], cancellationToken).ConfigureAwait(false);
        if (inspected.ExitCode != 0)
        {
            if (inspected.StandardError.Contains("No such network", StringComparison.OrdinalIgnoreCase) ||
                inspected.StandardError.Contains("not found", StringComparison.OrdinalIgnoreCase))
                return null;
            throw InstallerException.Refused(
                $"Docker could not inspect the Silo private network: {Redaction.SafeDiagnostic(inspected.StandardError)}");
        }
        using var document = JsonDocument.Parse(inspected.StandardOutput);
        var root = document.RootElement[0];
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("Labels", out var labelValues) && labelValues.ValueKind == JsonValueKind.Object)
        {
            foreach (var label in labelValues.EnumerateObject())
                if (label.Value.ValueKind == JsonValueKind.String) labels[label.Name] = label.Value.GetString()!;
        }
        var members = root.TryGetProperty("Containers", out var containers) && containers.ValueKind == JsonValueKind.Object
            ? containers.EnumerateObject().Select(static member => member.Name).ToHashSet(StringComparer.Ordinal)
            : [];
        return new DockerNetworkObservation(
            root.GetProperty("Name").GetString() ?? string.Empty,
            root.GetProperty("Id").GetString() ?? string.Empty,
            root.TryGetProperty("Driver", out var driver) ? driver.GetString() : null,
            root.TryGetProperty("Internal", out var isInternal) && isInternal.ValueKind == JsonValueKind.True,
            labels,
            members);
    }

    /// <summary>
    /// The CameraAgent container as Docker reports it now, or null when it does not exist. Read-only; any other daemon
    /// failure fails closed.
    /// </summary>
    public async Task<ApplicationContainerObservation?> ObserveApplicationContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        var inspected = await RunDockerRawAsync(["container", "inspect", containerName], cancellationToken).ConfigureAwait(false);
        if (inspected.ExitCode != 0)
        {
            if (inspected.StandardError.Contains("No such container", StringComparison.OrdinalIgnoreCase) ||
                inspected.StandardError.Contains("No such object", StringComparison.OrdinalIgnoreCase))
                return null;
            throw InstallerException.Refused($"Docker could not inspect the CameraAgent container: {Redaction.SafeDiagnostic(inspected.StandardError)}");
        }
        using var document = JsonDocument.Parse(inspected.StandardOutput);
        var container = document.RootElement[0];
        var state = container.GetProperty("State");
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (container.GetProperty("Config").TryGetProperty("Labels", out var labelValues) && labelValues.ValueKind == JsonValueKind.Object)
        {
            foreach (var label in labelValues.EnumerateObject())
                if (label.Value.ValueKind == JsonValueKind.String) labels[label.Name] = label.Value.GetString()!;
        }
        var networks = container.GetProperty("NetworkSettings").TryGetProperty("Networks", out var networkValue) &&
                       networkValue.ValueKind == JsonValueKind.Object
            ? networkValue.EnumerateObject().Select(static network => network.Name).ToArray()
            : [];
        DateTimeOffset? started = state.TryGetProperty("StartedAt", out var startedValue) && startedValue.ValueKind == JsonValueKind.String &&
                                  DateTimeOffset.TryParse(startedValue.GetString(), CultureInfo.InvariantCulture,
                                      DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
        var mounts = container.TryGetProperty("Mounts", out var mountValues) && mountValues.ValueKind == JsonValueKind.Array
            ? mountValues.EnumerateArray().Where(static mount => mount.ValueKind == JsonValueKind.Object).Select(static mount => (
                mount.TryGetProperty("Type", out var type) ? type.GetString() : null,
                mount.TryGetProperty("Source", out var source) ? source.GetString() : null,
                mount.TryGetProperty("Destination", out var destination) ? destination.GetString() : null,
                !mount.TryGetProperty("RW", out var writable) || writable.ValueKind != JsonValueKind.False)).ToArray()
            : [];
        var environment = container.GetProperty("Config").TryGetProperty("Env", out var env) && env.ValueKind == JsonValueKind.Array
            ? env.EnumerateArray().Select(static item => item.GetString()).OfType<string>().ToArray()
            : [];
        return new ApplicationContainerObservation(
            container.GetProperty("Id").GetString() ?? string.Empty,
            state.GetProperty("Running").GetBoolean(),
            state.TryGetProperty("Health", out var health) && health.ValueKind == JsonValueKind.Object ? health.GetProperty("Status").GetString() : null,
            container.GetProperty("Image").GetString(),
            started,
            labels,
            networks)
        {
            Mounts = mounts,
            Environment = environment
        };
    }

    /// <summary>
    /// Runs a read-only probe script in a throwaway container that joins <paramref name="applicationContainerId"/>'s network
    /// namespace and mounts its volumes read-only, so the store is reached exactly as CameraAgent reaches it: through its
    /// networks and resolver, with the endpoint and writer credential its own live settings mount gives it. The probe uses the verified local Silo image (never pulled), runs unprivileged with a
    /// read-only root and no capabilities, and takes its script, credential included, on standard input only.
    /// </summary>
    public async Task<ProcessResult> RunInApplicationNetworkAsync(
        string applicationContainerId, string siloImage, string platform, string user, Guid instanceId, string script,
        CancellationToken cancellationToken)
    {
        if (processRunner is not IProcessInputRunner inputRunner)
            throw new InstallerException("Silo verification requires a process runner that supplies standard input.");
        _ = await RunDockerAsync(["version", "--format", "{{.Server.Version}}"], cancellationToken).ConfigureAwait(false);
        return await inputRunner.RunWithInputAsync(
                "docker",
                [
                    "--host", endpoint!, "run", "--rm", "--interactive", "--pull", "never", "--platform", platform,
                    "--network", $"container:{applicationContainerId}", "--volumes-from", $"{applicationContainerId}:ro",
                    "--user", user, "--read-only",
                    "--tmpfs", "/tmp:rw,nosuid,nodev,noexec,size=16m", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                    "--label", $"io.hvo.skymonitor.instance-id={instanceId:D}", "--label", "io.hvo.skymonitor.component=SiloProbe",
                    "--entrypoint", "bash", siloImage, "-s"
                ],
                script + "\n",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> IsInternalNetworkAsync(string network, CancellationToken cancellationToken)
    {
        var inspected = await RunDockerRawAsync(["network", "inspect", "--format", "{{.Internal}}", network], cancellationToken)
            .ConfigureAwait(false);
        return inspected.ExitCode == 0 && string.Equals(inspected.StandardOutput.Trim(), "true", StringComparison.Ordinal);
    }

    /// <summary>
    /// Data-root exclusivity (contract 7.2): no container other than <paramref name="ownerContainer"/>, running or not,
    /// mounts the Silo data root, a path inside it, an ancestor of it, or the same directory under another name.
    /// </summary>
    public async Task EnsureSiloDataRootExclusiveAsync(string dataRoot, string? ownerContainer, CancellationToken cancellationToken)
    {
        var identities = ExistingAncestorIdentities(dataRoot);
        var listed = await RunDockerAsync(["container", "ls", "--all", "--quiet", "--no-trunc"], cancellationToken).ConfigureAwait(false);
        foreach (var id in listed.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var inspected = await RunDockerRawAsync(["container", "inspect", id], cancellationToken).ConfigureAwait(false);
            if (inspected.ExitCode != 0)
            {
                // Only Docker's own answer that the container is gone (removed since the listing) concludes it; any other
                // failure leaves its mounts unknown, so exclusivity is not proved.
                if (inspected.StandardError.Contains("No such container", StringComparison.OrdinalIgnoreCase) ||
                    inspected.StandardError.Contains("No such object", StringComparison.OrdinalIgnoreCase))
                    continue;
                throw InstallerException.Refused(
                    $"Docker could not inspect a container beside the Silo data root, so its exclusivity is not proved: {Redaction.SafeDiagnostic(inspected.StandardError)}");
            }
            using var document = JsonDocument.Parse(inspected.StandardOutput);
            var container = document.RootElement[0];
            var name = container.GetProperty("Name").GetString()?.TrimStart('/');
            if (ownerContainer is not null && string.Equals(name, ownerContainer, StringComparison.Ordinal)) continue;
            var references = container.GetProperty("Mounts").EnumerateArray().Any(mount =>
            {
                var source = mount.TryGetProperty("Source", out var value) ? value.GetString() : null;
                return source is not null &&
                       (source == dataRoot ||
                        source.StartsWith(dataRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                        dataRoot.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                        ReferencesSameNode(source, identities));
            });
            if (references)
                throw InstallerException.Refused("Another container references the Silo data root; it must belong to this instance's Silo alone.");
        }
    }
}
