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
/// and tags only <c>&lt;repository&gt;:&lt;version&gt;</c>, so a load cannot retag another image on the host;</item>
/// <item>every layer the manifest names is in the archive, the size the manifest gives it and the blob its digest
/// names, so Docker cannot reject the load for a missing or damaged layer.</item>
/// </list>
/// Docker and containerd read the layout, the index, the manifest and <c>manifest.json</c> into structures whose fields
/// they match regardless of case, so each of those objects may hold only the fields the release tool writes, spelled
/// exactly, and the configuration's names must be ASCII and distinct ignoring case (<see cref="ReleaseJson"/>).
/// A loader reads entries by their cleaned path and lets a later entry replace an earlier one, so the archive must spell
/// each entry one way and hold it once: every entry name must be the canonical spelling of its path (no <c>.</c> or
/// <c>..</c> segment, no empty segment, no leading <c>/</c>, no backslash, and a trailing <c>/</c> only on a
/// directory), must be part of the OCI image layout (<c>oci-layout</c>, <c>index.json</c>, <c>manifest.json</c>, the
/// <c>blobs/sha256</c> directories and the blobs the image uses), must appear once, and must be a regular file, or a
/// directory where the layout has one. Every blob is checked against the digest that names it, and its JSON is read
/// with <see cref="ReleaseJson"/>.
/// </summary>
internal static class ReleaseImageArchive
{
    /// <summary>The longest layout, index, manifest or configuration entry read into memory.</summary>
    private const int MaximumMetadataBytes = 4 * 1024 * 1024;

    private const string BlobPrefix = "blobs/sha256/";

    /// <summary>The files of an OCI image layout, as the release tool writes it.</summary>
    private static readonly string[] LayoutFiles = ["oci-layout", "index.json", "manifest.json"];

    /// <summary>The directories of an OCI image layout, which the release tool writes as entries of their own.</summary>
    private static readonly string[] LayoutDirectories = ["blobs", "blobs/sha256"];

    internal static readonly string[] IndexMediaTypes =
        ["application/vnd.oci.image.index.v1+json", "application/vnd.docker.distribution.manifest.list.v2+json"];

    internal static readonly string[] ManifestMediaTypes =
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
        var (entries, blobs) = await ReadEntriesAsync(stream, archive.Name, wanted, cancellationToken).ConfigureAwait(false);
        InstallerException Refusal(string reason)
            => new($"The offline archive {archive.Name} is not the image release {release.Tag} names: {reason}.");
        byte[] Entry(string name, string what)
            => entries.TryGetValue(name, out var bytes) ? bytes : throw Refusal($"it holds no {what}");
        // A blob is the content its descriptor gives: the digest that names it and the size the descriptor records.
        byte[] Blob(string digest, long? size, string what)
        {
            var bytes = Entry(BlobPrefix + Hex(digest), what);
            if (!string.Equals($"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}", digest, StringComparison.Ordinal))
            {
                throw Refusal($"its {what} is not the blob {digest} names");
            }
            if (size != bytes.Length)
            {
                throw Refusal($"its {what} is not the size its descriptor gives");
            }
            return bytes;
        }

        using (var layoutVersion = ReleaseJson.Parse(Entry("oci-layout", "OCI layout version"), $"the offline archive {archive.Name}'s oci-layout"))
        {
            OnlyFields(layoutVersion.RootElement, "oci-layout", Refusal, "imageLayoutVersion");
            if (Text(layoutVersion.RootElement, "imageLayoutVersion") != "1.0.0")
            {
                throw Refusal("its OCI layout is not version 1.0.0");
            }
        }

        var reference = $"{image.Repository}:{release.Version}";
        long? imageSize;
        using (var layout = ReleaseJson.Parse(Entry("index.json", "OCI layout index"), $"the offline archive {archive.Name}'s OCI layout index"))
        {
            OnlyFields(layout.RootElement, "OCI layout index", Refusal, "schemaVersion", "mediaType", "manifests");
            var top = Single(layout.RootElement, "manifests") ?? throw Refusal("its OCI layout does not name exactly one image");
            OnlyFields(top, "OCI layout index's image", Refusal, "mediaType", "digest", "size", "annotations");
            if (Text(top, "digest") != image.Digest)
            {
                throw Refusal($"its OCI layout names an image other than {image.Digest}");
            }
            imageSize = Size(top);
            var mediaType = Text(top, "mediaType");
            if (!(singlePlatform ? ManifestMediaTypes : IndexMediaTypes).Contains(mediaType, StringComparer.Ordinal))
            {
                throw Refusal(singlePlatform ? "its OCI layout does not name a platform manifest" : "its OCI layout does not name an image index");
            }
            // Docker's containerd store names the image it loads from these annotations.
            if (top.TryGetProperty("annotations", out var annotations))
            {
                OnlyFields(annotations, "OCI layout index's annotations", Refusal, "io.containerd.image.name", "org.opencontainers.image.ref.name");
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

        long? manifestSize = imageSize;
        if (!singlePlatform)
        {
            using var index = ReleaseJson.Parse(Blob(image.Digest, imageSize, "image index"), $"the offline archive {archive.Name}'s image index");
            if (index.RootElement.ValueKind != JsonValueKind.Object ||
                !index.RootElement.TryGetProperty("manifests", out var manifests) || manifests.ValueKind != JsonValueKind.Array)
            {
                throw Refusal("its image index lists no platforms");
            }
            OnlyFields(index.RootElement, "image index", Refusal, "schemaVersion", "mediaType", "manifests", "annotations");
            foreach (var entry in manifests.EnumerateArray())
            {
                OnlyFields(entry, "image index entry", Refusal, "mediaType", "digest", "size", "platform", "annotations");
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("platform", out var entryPlatform))
                {
                    OnlyFields(entryPlatform, "image index entry's platform", Refusal, "architecture", "os", "variant");
                }
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
            manifestSize = Size(matching[0]);
        }

        (string Digest, long Size)[] layerDescriptors;
        long? configSize;
        using (var manifest = ReleaseJson.Parse(Blob(archive.ManifestDigest, manifestSize, $"{archive.Platform} manifest"), $"the offline archive {archive.Name}'s {archive.Platform} manifest"))
        {
            var root = manifest.RootElement;
            OnlyFields(root, $"{archive.Platform} manifest", Refusal, "schemaVersion", "mediaType", "config", "layers", "annotations");
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("config", out var config) ||
                config.ValueKind != JsonValueKind.Object || Text(config, "digest") != archive.ConfigDigest)
            {
                throw Refusal($"its {archive.Platform} manifest does not name the configuration {archive.ConfigDigest}");
            }
            OnlyFields(config, $"{archive.Platform} manifest's configuration descriptor", Refusal, "mediaType", "digest", "size", "annotations");
            configSize = Size(config);
            if (!root.TryGetProperty("layers", out var layerValues) || layerValues.ValueKind != JsonValueKind.Array)
            {
                throw Refusal($"its {archive.Platform} manifest lists no layers");
            }
            foreach (var layer in layerValues.EnumerateArray())
            {
                OnlyFields(layer, $"{archive.Platform} manifest's layer descriptor", Refusal, "mediaType", "digest", "size", "annotations");
            }
            layerDescriptors = [.. layerValues.EnumerateArray().Select(layer => layer.ValueKind == JsonValueKind.Object &&
                                                                                Text(layer, "digest") is { } digest && IsDigest(digest) &&
                                                                                Size(layer) is { } size
                ? (digest, size)
                : throw Refusal($"its {archive.Platform} manifest names a layer without a digest and size"))];
        }
        string[] layers = [.. layerDescriptors.Select(static layer => BlobPrefix + Hex(layer.Digest))];

        // Docker opens every layer the image names while it loads, and refuses the load when one is missing or is not
        // the blob its digest names, which would be after the install has claimed its state.
        foreach (var (digest, size) in layerDescriptors)
        {
            if (!blobs.TryGetValue(BlobPrefix + Hex(digest), out var layer))
            {
                throw Refusal($"it holds no {archive.Platform} layer {digest}");
            }
            if (layer.Length != size)
            {
                throw Refusal($"its {archive.Platform} layer {digest} is not the size its manifest gives");
            }
            if (layer.Digest != digest)
            {
                throw Refusal($"its {archive.Platform} layer {digest} is not the blob that digest names");
            }
        }

        // Docker's classic store loads the image manifest.json describes rather than the OCI index, so it must describe
        // the same image and tag nothing else.
        {
            using var dockerManifest = ReleaseJson.Parse(Entry("manifest.json", "manifest.json"), $"the offline archive {archive.Name}'s manifest.json");
            var only = dockerManifest.RootElement.ValueKind == JsonValueKind.Array && dockerManifest.RootElement.GetArrayLength() == 1
                ? dockerManifest.RootElement[0]
                : default;
            // Docker reads RepoTags from any spelling it folds to that name, so only these exact fields may be present.
            OnlyFields(only, "manifest.json", Refusal, "Config", "RepoTags", "Layers");
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

        using var configuration = ReleaseJson.Parse(Blob(archive.ConfigDigest, configSize, $"{archive.Platform} configuration"), $"the offline archive {archive.Name}'s {archive.Platform} configuration");
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

        // Anything else under blobs/ is content no loader of this image reads, so the archive is not the one the release
        // tool wrote.
        var used = new HashSet<string>([.. wanted.Where(static name => name.StartsWith(BlobPrefix, StringComparison.Ordinal)), .. layers], StringComparer.Ordinal);
        if (blobs.Keys.FirstOrDefault(name => !used.Contains(name)) is { } unused)
        {
            throw Refusal($"it holds {unused}, which its image does not use");
        }
        return identity;
    }

    /// <summary>
    /// Reads the archive once. Every entry is checked: its name must be the canonical spelling of a path in the OCI image
    /// layout, it must appear once, and it must be a regular file, or a directory where the layout has one. The layout's
    /// files and the blobs named in <paramref name="wanted"/> are kept, each at most <see cref="MaximumMetadataBytes"/>,
    /// and every blob's size and SHA-256 are recorded; the layers are read past without being kept.
    /// </summary>
    private static async Task<(Dictionary<string, byte[]> Entries, Dictionary<string, BlobContent> Blobs)> ReadEntriesAsync(
        Stream stream,
        string name,
        HashSet<string> wanted,
        CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var blobs = new Dictionary<string, BlobContent>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await using var reader = new TarReader(stream, leaveOpen: true);
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
            {
                var entryName = CanonicalName(entry)
                                ?? throw new InstallerException(
                                    $"The offline archive {name} holds an entry named {Printable(entry.Name)}, which is not the canonical spelling of a path.");
                var isBlob = IsBlobName(entryName);
                if (!isBlob && !LayoutFiles.Contains(entryName, StringComparer.Ordinal) &&
                    !LayoutDirectories.Contains(entryName, StringComparer.Ordinal))
                {
                    throw new InstallerException(
                        $"The offline archive {name} holds an entry named {Printable(entryName)}, which is not part of an OCI image layout.");
                }
                if (LayoutDirectories.Contains(entryName, StringComparer.Ordinal))
                {
                    if (entry.EntryType != TarEntryType.Directory)
                    {
                        throw new InstallerException($"The offline archive {name} holds {entryName} as something other than a directory.");
                    }
                }
                else if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    throw new InstallerException($"The offline archive {name} holds {entryName} as something other than a file.");
                }
                if (!seen.Add(entryName))
                {
                    // A loader would read one of the two copies, and this check might have read the other.
                    throw new InstallerException($"The offline archive {name} holds {entryName} twice.");
                }
                if (entry.EntryType == TarEntryType.Directory)
                {
                    continue;
                }
                var data = entry.DataStream ?? Stream.Null;
                if (isBlob && !wanted.Contains(entryName))
                {
                    var digest = await SHA256.HashDataAsync(data, cancellationToken).ConfigureAwait(false);
                    blobs.Add(entryName, new BlobContent(entry.Length, $"sha256:{Convert.ToHexStringLower(digest)}"));
                    continue;
                }
                if (entry.Length > MaximumMetadataBytes)
                {
                    throw new InstallerException($"The offline archive {name} holds an unreadable {entryName}.");
                }
                var bytes = new byte[entry.Length];
                await data.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                entries.Add(entryName, bytes);
                if (isBlob)
                {
                    blobs.Add(entryName, new BlobContent(bytes.Length, $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}"));
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or EndOfStreamException)
        {
            throw new InstallerException($"The offline archive {name} is not a readable tar archive.", exception);
        }
        return (entries, blobs);
    }

    /// <summary>
    /// The path <paramref name="entry"/> names, or <see langword="null"/> when its name is not that path's one canonical
    /// spelling: an empty, <c>.</c> or <c>..</c> segment, a leading <c>/</c>, a backslash or a NUL, or a trailing
    /// <c>/</c> on anything but a directory. Docker and containerd clean each name before they use it, so any other
    /// spelling could reach a file this check did not read under that name.
    /// </summary>
    private static string? CanonicalName(TarEntry entry)
    {
        var name = entry.Name;
        if (entry.EntryType == TarEntryType.Directory && name.EndsWith('/'))
        {
            name = name[..^1];
        }
        if (name.Length == 0 || name[0] == '/' || name.Contains('\\', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }
        return name.Split('/').Any(static segment => segment.Length == 0 || segment is "." or "..") ? null : name;
    }

    private static bool IsBlobName(string name)
        => name.Length == BlobPrefix.Length + 64 && name.StartsWith(BlobPrefix, StringComparison.Ordinal) &&
           name[BlobPrefix.Length..].All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>An entry name quoted for a message, unless it holds characters a message should not carry.</summary>
    private static string Printable(string name)
        => name.Length is > 0 and <= 160 &&
           name.All(static character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or
               '.' or '/' or '-' or '_' or '+' or '@' or ':' or '\\')
            ? $"'{name}'"
            : "that the installer will not print";

    /// <summary>
    /// Refuses a field of <paramref name="element"/>, when it is an object, that is not exactly one of
    /// <paramref name="allowed"/>. Docker and containerd match these objects' fields regardless of case, so a field this
    /// check does not read by its exact name could still be one they act on, with a value this check never saw.
    /// </summary>
    private static void OnlyFields(JsonElement element, string what, Func<string, InstallerException> refusal, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                var field = property.Name.Length is > 0 and <= 64 &&
                            property.Name.All(static character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_')
                    ? $"'{property.Name}'"
                    : "whose name it will not print";
                throw refusal($"its {what} has a field {field}, which the installer does not accept");
            }
        }
    }

    /// <summary>The <c>size</c> a descriptor gives, or <see langword="null"/> when it gives none that is a byte count.</summary>
    private static long? Size(JsonElement descriptor)
        => descriptor.ValueKind == JsonValueKind.Object && descriptor.TryGetProperty("size", out var size) &&
           size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var length) && length >= 0
            ? length
            : null;

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

    /// <summary>A blob as the archive holds it: its size and the SHA-256 of its content.</summary>
    private readonly record struct BlobContent(long Length, string Digest);
}
