using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// Reads the CameraAgent image an offline archive holds from the archive itself, before Docker is asked to load it, so a
/// release whose archive holds another image is refused while nothing has been claimed, staged or imported. The archive's
/// bytes are already the ones release.json and SHA256SUMS give; this checks that what they describe is the release's
/// image:
/// <list type="bullet">
/// <item>the OCI layout names one image, the release's digest, and, when Docker's containerd store would name the
/// loaded image from it, names it <c>&lt;repository&gt;:&lt;version&gt;</c>;</item>
/// <item>the release's index lists the platform once, as the manifest release.json names;</item>
/// <item>that manifest names the configuration release.json names;</item>
/// <item>the configuration is for the platform, and is labelled CameraAgent at the release's version and commit;</item>
/// <item><c>manifest.json</c>, which Docker's classic store loads instead, names that configuration and those layers,
/// and tags only <c>&lt;repository&gt;:&lt;version&gt;</c>, so a load cannot retag another image on the host.</item>
/// </list>
/// Every blob is checked against the digest that names it, its JSON is read with <see cref="ReleaseJson"/>, and an entry
/// the check reads that appears twice, or is not a regular file, is refused.
/// </summary>
internal static class ReleaseImageArchive
{
    /// <summary>The longest layout, index, manifest or configuration entry read into memory.</summary>
    private const int MaximumMetadataBytes = 4 * 1024 * 1024;

    private const string BlobPrefix = "blobs/sha256/";

    private static readonly string[] IndexMediaTypes =
        ["application/vnd.oci.image.index.v1+json", "application/vnd.docker.distribution.manifest.list.v2+json"];

    private static readonly string[] ManifestMediaTypes =
        ["application/vnd.oci.image.manifest.v1+json", "application/vnd.docker.distribution.manifest.v2+json"];

    /// <summary>
    /// The image <paramref name="archive"/> holds for its platform, as the identity Docker's classic store gives a loaded
    /// image (its configuration digest), read from <paramref name="stream"/>. Throws <see cref="InstallerException"/>
    /// naming what disagrees with release.json.
    /// </summary>
    public static async Task<ImageInstallationIdentity> ReadAsync(
        Stream stream,
        ReleaseManifest release,
        ReleaseImage image,
        OfflineArchive archive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(archive);
        var singlePlatform = string.Equals(image.Digest, archive.ManifestDigest, StringComparison.Ordinal);
        var wanted = new HashSet<string>(StringComparer.Ordinal)
        {
            "index.json", "manifest.json", BlobPrefix + Hex(archive.ManifestDigest), BlobPrefix + Hex(archive.ConfigDigest)
        };
        if (!singlePlatform)
        {
            wanted.Add(BlobPrefix + Hex(image.Digest));
        }
        var entries = await ReadEntriesAsync(stream, archive.Name, wanted, cancellationToken).ConfigureAwait(false);
        InstallerException Refusal(string reason)
            => new($"The offline archive {archive.Name} is not the image release {release.Tag} names: {reason}.");
        byte[] Entry(string name, string what)
            => entries.TryGetValue(name, out var bytes) ? bytes : throw Refusal($"it holds no {what}");
        byte[] Blob(string digest, string what)
        {
            var bytes = Entry(BlobPrefix + Hex(digest), what);
            if (!string.Equals($"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}", digest, StringComparison.Ordinal))
            {
                throw Refusal($"its {what} is not the blob {digest} names");
            }
            return bytes;
        }

        var reference = $"{image.Repository}:{release.Version}";
        using (var layout = ReleaseJson.Parse(Entry("index.json", "OCI layout index"), $"the offline archive {archive.Name}'s OCI layout index"))
        {
            var top = Single(layout.RootElement, "manifests") ?? throw Refusal("its OCI layout does not name exactly one image");
            if (Text(top, "digest") != image.Digest)
            {
                throw Refusal($"its OCI layout names an image other than {image.Digest}");
            }
            var mediaType = Text(top, "mediaType");
            if (!(singlePlatform ? ManifestMediaTypes : IndexMediaTypes).Contains(mediaType, StringComparer.Ordinal))
            {
                throw Refusal(singlePlatform ? "its OCI layout does not name a platform manifest" : "its OCI layout does not name an image index");
            }
            // Docker's containerd store names the image it loads from these annotations.
            if (top.TryGetProperty("annotations", out var annotations))
            {
                if (annotations.ValueKind != JsonValueKind.Object ||
                    (annotations.TryGetProperty("io.containerd.image.name", out var name) && name.ValueKind != JsonValueKind.Null &&
                     !(name.ValueKind == JsonValueKind.String && name.GetString() == reference)) ||
                    (annotations.TryGetProperty("org.opencontainers.image.ref.name", out var refName) && refName.ValueKind != JsonValueKind.Null &&
                     !(refName.ValueKind == JsonValueKind.String && refName.GetString() == release.Version)))
                {
                    throw Refusal($"its OCI layout would name the loaded image something other than {reference}");
                }
            }
        }

        if (!singlePlatform)
        {
            using var index = ReleaseJson.Parse(Blob(image.Digest, "image index"), $"the offline archive {archive.Name}'s image index");
            if (index.RootElement.ValueKind != JsonValueKind.Object ||
                !index.RootElement.TryGetProperty("manifests", out var manifests) || manifests.ValueKind != JsonValueKind.Array)
            {
                throw Refusal("its image index lists no platforms");
            }
            var matching = manifests.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.Object &&
                                entry.TryGetProperty("platform", out var platform) && platform.ValueKind == JsonValueKind.Object &&
                                $"{Text(platform, "os")}/{Text(platform, "architecture")}" == archive.Platform)
                .ToArray();
            if (matching.Length != 1 || Text(matching[0], "digest") != archive.ManifestDigest ||
                !ManifestMediaTypes.Contains(Text(matching[0], "mediaType"), StringComparer.Ordinal))
            {
                throw Refusal($"its image index does not list {archive.Platform} once, as {archive.ManifestDigest}");
            }
        }

        string[] layers;
        using (var manifest = ReleaseJson.Parse(Blob(archive.ManifestDigest, $"{archive.Platform} manifest"), $"the offline archive {archive.Name}'s {archive.Platform} manifest"))
        {
            var root = manifest.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("config", out var config) ||
                config.ValueKind != JsonValueKind.Object || Text(config, "digest") != archive.ConfigDigest)
            {
                throw Refusal($"its {archive.Platform} manifest does not name the configuration {archive.ConfigDigest}");
            }
            if (!root.TryGetProperty("layers", out var layerValues) || layerValues.ValueKind != JsonValueKind.Array)
            {
                throw Refusal($"its {archive.Platform} manifest lists no layers");
            }
            layers = [.. layerValues.EnumerateArray().Select(layer => layer.ValueKind == JsonValueKind.Object && Text(layer, "digest") is { } digest &&
                                                                          IsDigest(digest)
                ? BlobPrefix + Hex(digest)
                : throw Refusal($"its {archive.Platform} manifest names a layer without a digest"))];
        }

        // Docker's classic store loads the image manifest.json describes rather than the OCI index, so it must describe
        // the same image and tag nothing else.
        if (entries.TryGetValue("manifest.json", out var dockerManifestBytes))
        {
            using var dockerManifest = ReleaseJson.Parse(dockerManifestBytes, $"the offline archive {archive.Name}'s manifest.json");
            var only = dockerManifest.RootElement.ValueKind == JsonValueKind.Array && dockerManifest.RootElement.GetArrayLength() == 1
                ? dockerManifest.RootElement[0]
                : default;
            var describesTheImage = only.ValueKind == JsonValueKind.Object &&
                                    Text(only, "Config") == BlobPrefix + Hex(archive.ConfigDigest) &&
                                    only.TryGetProperty("Layers", out var dockerLayers) && dockerLayers.ValueKind == JsonValueKind.Array &&
                                    dockerLayers.EnumerateArray().Select(static layer => layer.ValueKind == JsonValueKind.String ? layer.GetString() : null)
                                        .SequenceEqual(layers, StringComparer.Ordinal);
            var tagsOnlyTheRelease = only.ValueKind == JsonValueKind.Object &&
                                     (!only.TryGetProperty("RepoTags", out var tags) || tags.ValueKind == JsonValueKind.Null ||
                                      (tags.ValueKind == JsonValueKind.Array && tags.EnumerateArray().All(tag =>
                                          tag.ValueKind == JsonValueKind.String && tag.GetString() == reference)));
            if (!describesTheImage || !tagsOnlyTheRelease)
            {
                throw Refusal($"its manifest.json does not describe that image under {reference} alone");
            }
        }

        using var configuration = ReleaseJson.Parse(Blob(archive.ConfigDigest, $"{archive.Platform} configuration"), $"the offline archive {archive.Name}'s {archive.Platform} configuration");
        var configurationRoot = configuration.RootElement;
        if (configurationRoot.ValueKind != JsonValueKind.Object ||
            $"{Text(configurationRoot, "os")}/{Text(configurationRoot, "architecture")}" != archive.Platform)
        {
            throw Refusal($"its configuration is not for {archive.Platform}");
        }
        var labels = configurationRoot.TryGetProperty("config", out var runtime) && runtime.ValueKind == JsonValueKind.Object &&
                     runtime.TryGetProperty("Labels", out var labelValues)
            ? labelValues
            : default;
        var identity = CameraAgentImageLabels.Identity(
            "archive", archive.ConfigDigest, archive.ConfigDigest, archive.Platform["linux/".Length..], archive.Sha256, labels);
        if (identity.Component != "CameraAgent" || CameraAgentImageLabels.Label(labels, "org.opencontainers.image.version") != release.Version ||
            identity.SourceRevision != release.Commit)
        {
            throw new InstallerException(
                $"The offline archive {archive.Name} holds an image that is not labelled as CameraAgent {release.Version} from " +
                $"commit {release.Commit}, as release {release.Tag} requires.");
        }
        return identity;
    }

    /// <summary>
    /// The regular-file entries of the archive named in <paramref name="wanted"/>, each at most
    /// <see cref="MaximumMetadataBytes"/>. Anything else, including the layers, is read past without being kept.
    /// </summary>
    private static async Task<Dictionary<string, byte[]>> ReadEntriesAsync(
        Stream stream,
        string name,
        HashSet<string> wanted,
        CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            await using var reader = new TarReader(stream, leaveOpen: true);
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
            {
                // A loader reads "./index.json" as "index.json", so both spellings are one entry here.
                var entryName = entry.Name;
                while (entryName.StartsWith("./", StringComparison.Ordinal))
                {
                    entryName = entryName[2..];
                }
                if (!wanted.Contains(entryName))
                {
                    continue;
                }
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    throw new InstallerException($"The offline archive {name} holds {entryName} as something other than a file.");
                }
                if (entries.ContainsKey(entryName))
                {
                    // A loader would read one of the two copies, and this check might have read the other.
                    throw new InstallerException($"The offline archive {name} holds {entryName} twice.");
                }
                if (entry.Length > MaximumMetadataBytes || entry.DataStream is null)
                {
                    throw new InstallerException($"The offline archive {name} holds an unreadable {entryName}.");
                }
                var bytes = new byte[entry.Length];
                await entry.DataStream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                entries.Add(entryName, bytes);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or EndOfStreamException)
        {
            throw new InstallerException($"The offline archive {name} is not a readable tar archive.", exception);
        }
        return entries;
    }

    /// <summary>The one element of <paramref name="name"/>'s array, or <see langword="null"/> unless there is exactly one.</summary>
    private static JsonElement? Single(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var array) &&
           array.ValueKind == JsonValueKind.Array && array.GetArrayLength() == 1 && array[0].ValueKind == JsonValueKind.Object
            ? array[0]
            : null;

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsDigest(string value)
        => value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
           value[7..].All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Hex(string digest) => digest["sha256:".Length..];
}
