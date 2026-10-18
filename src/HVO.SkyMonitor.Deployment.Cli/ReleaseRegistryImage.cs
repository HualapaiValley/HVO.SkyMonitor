using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// Reads the CameraAgent image a release names from its registry before Docker is asked to pull it, so a release whose
/// registry image is not the release's image is refused while nothing has been imported, claimed or staged. It reads,
/// by the digest release.json names, through <c>docker buildx imagetools inspect</c> (buildx 0.8 or later, which added the
/// <c>--format</c> template and its <c>.Image</c> field), which uses this user's Docker credentials and credential helpers,
/// so the installer never handles registry credentials. A buildx that is missing or older fails the read, and the
/// registry source is refused before anything is pulled, naming the archive source:
/// <list type="bullet">
/// <item>the release's image index, or for a one-platform release its manifest, exactly as the registry serves it,
/// checked against the release's digest;</item>
/// <item>the index's one entry for the platform, and that platform manifest, exactly as served, checked against the
/// digest the index gives it;</item>
/// <item>the configuration that manifest names, which buildx fetches by its digest: it must be for the platform and be
/// labelled CameraAgent at the release's version and commit.</item>
/// </list>
/// When release.json also names the platform's manifest and configuration (it does when the release has an offline
/// archive for the platform), the registry's must be those. The pulled image is then held to the same identities
/// (<see cref="DockerClient.ImportReleaseImageAsync"/>), which remain the second check.
/// </summary>
internal static class ReleaseRegistryImage
{
    /// <summary>The longest index, manifest or configuration read into memory.</summary>
    private const int MaximumMetadataBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The configuration identity of the release's image for its platform, with the selection extended by every identity
    /// the registry gave it (the release's digest, the platform manifest's and the configuration's). Throws
    /// <see cref="InstallerException"/> when the registry cannot be read or does not serve the release's image. Reading
    /// pulls nothing, so either way that is a refusal (exit 3, #1315).
    /// </summary>
    public static async Task<(ImageInstallationIdentity Image, ReleaseImageSelection Release)> ReadAsync(
        ReleaseImageSelection release,
        Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> runDocker,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadReleaseImageAsync(release, runDocker, cancellationToken).ConfigureAwait(false);
        }
        catch (InstallerException exception) when (!exception.IsRefusal)
        {
            throw InstallerException.Refused(exception.Message, exception);
        }
    }

    private static async Task<(ImageInstallationIdentity Image, ReleaseImageSelection Release)> ReadReleaseImageAsync(
        ReleaseImageSelection release,
        Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> runDocker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(runDocker);
        var at = release.DigestReference.IndexOf('@', StringComparison.Ordinal);
        var repository = release.DigestReference[..at];
        var digest = release.DigestReference[(at + 1)..];
        InstallerException Refusal(string reason)
            => new($"The CameraAgent image {release.DigestReference} in its registry is not the {release.Platform} image release " +
                   $"{release.ReleaseTag} names: {reason}. Nothing was pulled.");

        async Task<string> InspectAsync(IReadOnlyList<string> arguments)
        {
            var result = await runDocker(["buildx", "imagetools", "inspect", .. arguments], cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InstallerException(
                    $"The installer cannot read {release.DigestReference} from its registry before pulling it, so it pulls nothing " +
                    $"({Redaction.SafeDiagnostic(result.StandardError)}). Install from the release's offline archive, the default " +
                    "image source (leave out --image-source registry), or let this user read the registry with 'docker buildx " +
                    "imagetools inspect' (buildx 0.8 or later).");
            }
            if (result.StandardOutput.Length > MaximumMetadataBytes)
            {
                throw Refusal("its metadata is larger than the installer reads");
            }
            return result.StandardOutput;
        }

        // A manifest or index as the registry serves it, which must be the content the digest names. buildx prints it
        // unchanged; a trailing newline it may add is not part of the content.
        async Task<JsonDocument> ReadManifestAsync(string manifestDigest, string what)
        {
            var output = await InspectAsync(["--raw", $"{repository}@{manifestDigest}"]).ConfigureAwait(false);
            var bytes = Encoding.UTF8.GetBytes(output);
            if (!Names(manifestDigest, bytes) && !(bytes.Length > 0 && bytes[^1] == (byte)'\n' && Names(manifestDigest, bytes.AsSpan(0, bytes.Length - 1))))
            {
                throw Refusal($"its {what} is not the content {manifestDigest} names");
            }
            return ReleaseJson.Parse(bytes, $"the registry's {what} for {release.DigestReference}");
        }

        string manifestDigest;
        string configDigest;
        using (var top = await ReadManifestAsync(digest, "image").ConfigureAwait(false))
        {
            var mediaType = Text(top.RootElement, "mediaType");
            JsonDocument? platformManifest = null;
            try
            {
                if (ReleaseImageArchive.IndexMediaTypes.Contains(mediaType, StringComparer.Ordinal))
                {
                    var matching = top.RootElement.TryGetProperty("manifests", out var manifests) && manifests.ValueKind == JsonValueKind.Array
                        ? manifests.EnumerateArray()
                            .Where(entry => entry.ValueKind == JsonValueKind.Object &&
                                            entry.TryGetProperty("platform", out var platform) && platform.ValueKind == JsonValueKind.Object &&
                                            $"{Text(platform, "os")}/{Text(platform, "architecture")}" == release.Platform)
                            .ToArray()
                        : [];
                    if (matching.Length != 1 || Text(matching[0], "digest") is not { } listed || !IsDigest(listed) ||
                        !ReleaseImageArchive.ManifestMediaTypes.Contains(Text(matching[0], "mediaType"), StringComparer.Ordinal))
                    {
                        throw Refusal($"its image index does not list {release.Platform} once");
                    }
                    manifestDigest = listed;
                    platformManifest = await ReadManifestAsync(manifestDigest, $"{release.Platform} manifest").ConfigureAwait(false);
                    if (!ReleaseImageArchive.ManifestMediaTypes.Contains(Text(platformManifest.RootElement, "mediaType"), StringComparer.Ordinal))
                    {
                        throw Refusal($"its {release.Platform} manifest is not a platform manifest");
                    }
                }
                else if (ReleaseImageArchive.ManifestMediaTypes.Contains(mediaType, StringComparer.Ordinal))
                {
                    manifestDigest = digest;
                }
                else
                {
                    throw Refusal("it is neither an image index nor a platform manifest");
                }
                if (release.ImageIds is { } expected && !expected.Contains(manifestDigest, StringComparer.Ordinal))
                {
                    throw Refusal($"its {release.Platform} manifest is not the one release.json names");
                }
                var manifest = (platformManifest ?? top).RootElement;
                configDigest = manifest.ValueKind == JsonValueKind.Object && manifest.TryGetProperty("config", out var config) &&
                               Text(config, "digest") is { } named && IsDigest(named)
                    ? named
                    : throw Refusal($"its {release.Platform} manifest names no configuration");
                if (!manifest.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
                {
                    throw Refusal($"its {release.Platform} manifest lists no layers");
                }
                if (release.ImageIds is { } expectedIds && !expectedIds.Contains(configDigest, StringComparer.Ordinal))
                {
                    throw Refusal($"its {release.Platform} configuration is not the one release.json names");
                }
            }
            finally
            {
                platformManifest?.Dispose();
            }
        }

        // buildx fetches the configuration by the digest the manifest names and prints it as the image's configuration.
        var configurationOutput = await InspectAsync(["--format", "{{json .Image}}", $"{repository}@{manifestDigest}"])
            .ConfigureAwait(false);
        using var configuration = ReleaseJson.Parse(
            Encoding.UTF8.GetBytes(configurationOutput), $"the registry's {release.Platform} configuration for {release.DigestReference}");
        var root = configuration.RootElement;
        if (root.ValueKind != JsonValueKind.Object || $"{Text(root, "os")}/{Text(root, "architecture")}" != release.Platform)
        {
            throw Refusal($"its configuration is not for {release.Platform}");
        }
        var labels = root.TryGetProperty("config", out var runtime) && runtime.ValueKind == JsonValueKind.Object &&
                     runtime.TryGetProperty("Labels", out var labelValues)
            ? labelValues
            : default;
        var identity = CameraAgentImageLabels.Identity(
            "registry", release.DigestReference, configDigest, release.Platform["linux/".Length..], null, labels);
        if (identity.Component != "CameraAgent" || CameraAgentImageLabels.Label(labels, "org.opencontainers.image.version") != release.Version ||
            identity.SourceRevision != release.Commit ||
            CameraAgentImageLabels.Label(labels, "org.opencontainers.image.created") != release.Created)
        {
            throw new InstallerException(
                $"The CameraAgent image {release.DigestReference} in its registry is not labelled as CameraAgent {release.Version} " +
                $"from commit {release.Commit}, created {release.Created}, as release {release.ReleaseTag} requires. Nothing was pulled.");
        }
        return (identity, release with { ImageIds = [.. new[] { digest, manifestDigest, configDigest }.Distinct(StringComparer.Ordinal)] });
    }

    private static bool Names(string digest, ReadOnlySpan<byte> bytes)
        => string.Equals($"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}", digest, StringComparison.Ordinal);

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsDigest(string value)
        => value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
           value[7..].All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
