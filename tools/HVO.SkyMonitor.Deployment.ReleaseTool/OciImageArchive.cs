using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HVO.SkyMonitor.Deployment.ReleaseTool;

/// <summary>A release's offline archive of its image for one platform, as release.json describes it.</summary>
internal sealed record OfflineImageArchive(
    string Name,
    string Platform,
    long Size,
    string Sha256,
    string ManifestDigest,
    string ConfigDigest);

/// <summary>
/// Splits the multi-platform OCI archive a release image was built into (<c>docker buildx build --output
/// type=oci</c>), and pushed from byte for byte, into one offline archive per platform that <c>docker image load</c>
/// reads with either of Docker's image stores (install contract section 4, as RoofController's release does). Each holds
/// the image the registry serves under the release's digest (the index over the platforms, or for a one-platform release
/// the platform's manifest itself), that platform's manifest, its configuration and its layers, plus a
/// <c>manifest.json</c> for Docker's classic store. Every blob is checked against the digest that names
/// it, and the image must carry the release's version, commit and created time, and one state contract on every platform,
/// before anything is written.
/// </summary>
internal static class OciImageArchive
{
    /// <summary>The longest index, manifest or configuration blob read into memory.</summary>
    private const long MaximumMetadataBytes = 4 * 1024 * 1024;

    private const string BlobPrefix = "blobs/sha256/";

    /// <summary>The label an image declares its state contract in (install contract section 6).</summary>
    private const string StateContractsLabel = "io.hvo.skymonitor.state-contracts";

    private static readonly string[] StoreMembers = ["reads", "writes"];
    private static readonly string[] ReadsMembers = ["max", "min"];

    private static readonly string[] IndexMediaTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json"
    ];

    private static readonly string[] ManifestMediaTypes =
    [
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json"
    ];

    /// <summary>
    /// Writes one offline archive per platform of <paramref name="platforms"/> into <paramref name="output"/>, named
    /// <c>&lt;image&gt;-&lt;version&gt;-linux-&lt;arch&gt;.oci.tar</c>, and returns the release image's digest with the
    /// archives and the image's state contract (<see cref="ReadStateContracts"/>), which every platform's image declares
    /// alike. The source must hold exactly one image: an index listing exactly those platforms, or, for one platform,
    /// that platform's manifest.
    /// </summary>
    public static async Task<(string IndexDigest, IReadOnlyList<OfflineImageArchive> Archives, JsonObject StateContracts)> SplitAsync(
        string source,
        string imageName,
        string repository,
        string version,
        string commit,
        IReadOnlyList<string> platforms,
        string output,
        DateTimeOffset created,
        CancellationToken cancellationToken)
    {
        var work = Directory.CreateTempSubdirectory("hvo-release-oci-");
        try
        {
            await TarFile.ExtractToDirectoryAsync(source, work.FullName, overwriteFiles: false, cancellationToken)
                .ConfigureAwait(false);
            // The archive is this build's own output; a link inside it is still refused rather than followed.
            if (work.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Any(static entry => entry.LinkTarget is not null))
            {
                throw new ReleaseToolException("The image archive contains a link.");
            }
            var layoutPath = Path.Combine(work.FullName, "index.json");
            if (!File.Exists(layoutPath))
            {
                throw new ReleaseToolException("The image archive is not an OCI archive: it has no index.json.");
            }
            using var layout = JsonDocument.Parse(await File.ReadAllBytesAsync(layoutPath, cancellationToken).ConfigureAwait(false));
            if (!layout.RootElement.TryGetProperty("manifests", out var entries) || entries.ValueKind != JsonValueKind.Array ||
                entries.GetArrayLength() != 1)
            {
                throw new ReleaseToolException("The image archive must hold exactly one image.");
            }
            var top = Descriptor(entries[0], "the archive's image");
            if (platforms.Count == 1 && ManifestMediaTypes.Contains(top.MediaType, StringComparer.Ordinal))
            {
                // buildx writes a one-platform build without provenance as that platform's manifest, with no index over
                // it. The release then names the manifest itself, which is also what the registry serves.
                var (archive, contracts) = await WritePlatformAsync(
                    work.FullName, null, null, top, platforms[0], imageName, repository, version, commit, output, created,
                    cancellationToken).ConfigureAwait(false);
                return (top.Digest, [archive], contracts);
            }
            if (!IndexMediaTypes.Contains(top.MediaType, StringComparer.Ordinal))
            {
                throw new ReleaseToolException("The image archive's image is not an index of platforms.");
            }
            var indexBytes = await ReadBlobAsync(work.FullName, top, "the image index", cancellationToken).ConfigureAwait(false);
            using var index = JsonDocument.Parse(indexBytes);
            var byPlatform = new Dictionary<string, Descriptor>(StringComparer.Ordinal);
            foreach (var entry in index.RootElement.TryGetProperty("manifests", out var manifests) &&
                                  manifests.ValueKind == JsonValueKind.Array
                         ? manifests.EnumerateArray()
                         : throw new ReleaseToolException("The image index lists no manifests."))
            {
                var descriptor = Descriptor(entry, "an image index entry");
                var platform = entry.TryGetProperty("platform", out var platformValue) && platformValue.ValueKind == JsonValueKind.Object
                    ? $"{Text(platformValue, "os")}/{Text(platformValue, "architecture")}"
                    : "unknown";
                if (!ManifestMediaTypes.Contains(descriptor.MediaType, StringComparer.Ordinal) ||
                    !platforms.Contains(platform, StringComparer.Ordinal) || !byPlatform.TryAdd(platform, descriptor))
                {
                    throw new ReleaseToolException(
                        $"The image index must list one image for each of {string.Join(", ", platforms)} and nothing else.");
                }
            }
            if (byPlatform.Count != platforms.Count)
            {
                throw new ReleaseToolException(
                    $"The image index must list one image for each of {string.Join(", ", platforms)} and nothing else.");
            }

            var archives = new List<OfflineImageArchive>();
            JsonObject? stateContracts = null;
            foreach (var platform in platforms)
            {
                var (archive, contracts) = await WritePlatformAsync(
                    work.FullName, top, indexBytes, byPlatform[platform], platform, imageName, repository, version, commit,
                    output, created, cancellationToken).ConfigureAwait(false);
                // One release declares one state contract, so its platforms' images declare the same one.
                if (stateContracts is not null && !JsonNode.DeepEquals(stateContracts, contracts))
                {
                    throw new ReleaseToolException(
                        $"The {platform} image's {StateContractsLabel} label is not the {platforms[0]} image's.");
                }
                stateContracts ??= contracts;
                archives.Add(archive);
            }
            return (top.Digest, archives, stateContracts!);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new ReleaseToolException($"The image archive cannot be read: {exception.Message}", exception);
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    private static async Task<(OfflineImageArchive Archive, JsonObject StateContracts)> WritePlatformAsync(
        string root,
        Descriptor? top,
        byte[]? indexBytes,
        Descriptor manifestDescriptor,
        string platform,
        string imageName,
        string repository,
        string version,
        string commit,
        string output,
        DateTimeOffset created,
        CancellationToken cancellationToken)
    {
        var manifestBytes = await ReadBlobAsync(root, manifestDescriptor, $"the {platform} manifest", cancellationToken)
            .ConfigureAwait(false);
        using var manifest = JsonDocument.Parse(manifestBytes);
        var config = manifest.RootElement.TryGetProperty("config", out var configValue)
            ? Descriptor(configValue, $"the {platform} configuration")
            : throw new ReleaseToolException($"The {platform} manifest names no configuration.");
        var layers = manifest.RootElement.TryGetProperty("layers", out var layerValues) && layerValues.ValueKind == JsonValueKind.Array
            ? layerValues.EnumerateArray().Select(layer => Descriptor(layer, $"a {platform} layer")).ToArray()
            : throw new ReleaseToolException($"The {platform} manifest lists no layers.");
        var configBytes = await ReadBlobAsync(root, config, $"the {platform} configuration", cancellationToken).ConfigureAwait(false);
        JsonObject stateContracts;
        using (var configuration = JsonDocument.Parse(configBytes))
        {
            var operatingSystem = Text(configuration.RootElement, "os");
            var architecture = Text(configuration.RootElement, "architecture");
            if ($"{operatingSystem}/{architecture}" != platform)
            {
                throw new ReleaseToolException($"The {platform} image's configuration is for another platform.");
            }
            // The release's image names the release: the installer requires these labels, and a release whose image
            // does not carry them could never be installed.
            var labels = configuration.RootElement.TryGetProperty("config", out var runtime) &&
                         runtime.TryGetProperty("Labels", out var labelValues) && labelValues.ValueKind == JsonValueKind.Object
                ? labelValues
                : default;
            if (Label(labels, "io.hvo.skymonitor.component") != "CameraAgent" ||
                Label(labels, "org.opencontainers.image.version") != version ||
                Label(labels, "org.opencontainers.image.revision") != commit)
            {
                throw new ReleaseToolException(
                    $"The {platform} image is not labelled as CameraAgent {version} from commit {commit}.");
            }
            // Its creation time is the release's (install contract section 1), never the base image's.
            var createdText = created.ToString(Program.CreatedFormat, CultureInfo.InvariantCulture);
            if (Label(labels, "org.opencontainers.image.created") != createdText)
            {
                throw new ReleaseToolException(
                    $"The {platform} image's org.opencontainers.image.created label is not the release's created time, {createdText}.");
            }
            stateContracts = ReadStateContracts(Label(labels, StateContractsLabel), platform);
        }
        foreach (var layer in layers)
        {
            await VerifyBlobAsync(root, layer, $"a {platform} layer", cancellationToken).ConfigureAwait(false);
        }

        var name = $"{imageName}-{version}-{platform.Replace('/', '-')}.oci.tar";
        var path = Path.Combine(output, name);
        var reference = $"{repository}:{version}";
        var image = top ?? manifestDescriptor;
        var layoutIndex = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["schemaVersion"] = 2,
            ["mediaType"] = "application/vnd.oci.image.index.v1+json",
            ["manifests"] = new JsonArray(new JsonObject
            {
                ["mediaType"] = image.MediaType,
                ["digest"] = image.Digest,
                ["size"] = image.Size,
                ["annotations"] = new JsonObject
                {
                    ["io.containerd.image.name"] = reference,
                    ["org.opencontainers.image.ref.name"] = version
                }
            })
        }.ToJsonString());
        var dockerManifest = Encoding.UTF8.GetBytes(new JsonArray(new JsonObject
        {
            ["Config"] = BlobPrefix + config.Hex,
            ["RepoTags"] = new JsonArray(reference),
            ["Layers"] = new JsonArray([.. layers.Select(static layer => (JsonNode)(BlobPrefix + layer.Hex))])
        }).ToJsonString());
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
        {
            using var tar = new TarWriter(file, TarEntryFormat.Ustar, leaveOpen: true);
            await AddAsync(tar, "oci-layout", "{\"imageLayoutVersion\":\"1.0.0\"}"u8.ToArray(), created, cancellationToken).ConfigureAwait(false);
            await AddAsync(tar, "index.json", layoutIndex, created, cancellationToken).ConfigureAwait(false);
            await AddAsync(tar, "manifest.json", dockerManifest, created, cancellationToken).ConfigureAwait(false);
            await AddDirectoryAsync(tar, "blobs/", created, cancellationToken).ConfigureAwait(false);
            await AddDirectoryAsync(tar, BlobPrefix, created, cancellationToken).ConfigureAwait(false);
            var written = new HashSet<string>(StringComparer.Ordinal) { manifestDescriptor.Hex, config.Hex };
            if (top is not null && indexBytes is not null)
            {
                await AddAsync(tar, BlobPrefix + top.Hex, indexBytes, created, cancellationToken).ConfigureAwait(false);
                written.Add(top.Hex);
            }
            await AddAsync(tar, BlobPrefix + manifestDescriptor.Hex, manifestBytes, created, cancellationToken).ConfigureAwait(false);
            await AddAsync(tar, BlobPrefix + config.Hex, configBytes, created, cancellationToken).ConfigureAwait(false);
            foreach (var layer in layers.Where(layer => written.Add(layer.Hex)))
            {
                await using var stream = File.OpenRead(BlobPath(root, layer));
                await AddAsync(tar, BlobPrefix + layer.Hex, stream, created, cancellationToken).ConfigureAwait(false);
            }
        }
        await using var archive = File.OpenRead(path);
        var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(archive, cancellationToken).ConfigureAwait(false));
        return (new OfflineImageArchive(name, platform, archive.Length, sha256, manifestDescriptor.Digest, config.Digest), stateContracts);
    }

    private static Task AddAsync(TarWriter tar, string name, byte[] bytes, DateTimeOffset created, CancellationToken cancellationToken)
        => AddAsync(tar, name, new MemoryStream(bytes, writable: false), created, cancellationToken);

    private static async Task AddAsync(TarWriter tar, string name, Stream data, DateTimeOffset created, CancellationToken cancellationToken)
    {
        await using (data.ConfigureAwait(false))
        {
            await tar.WriteEntryAsync(new UstarTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = data,
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                ModificationTime = created,
                Uid = 0,
                Gid = 0,
                UserName = "root",
                GroupName = "root"
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task AddDirectoryAsync(TarWriter tar, string name, DateTimeOffset created, CancellationToken cancellationToken)
        => tar.WriteEntryAsync(new UstarTarEntry(TarEntryType.Directory, name)
        {
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead |
                   UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
            ModificationTime = created,
            Uid = 0,
            Gid = 0,
            UserName = "root",
            GroupName = "root"
        }, cancellationToken);

    private static async Task<byte[]> ReadBlobAsync(string root, Descriptor descriptor, string what, CancellationToken cancellationToken)
    {
        if (descriptor.Size > MaximumMetadataBytes)
        {
            throw new ReleaseToolException($"In the image archive, {what} is larger than {MaximumMetadataBytes} bytes.");
        }
        var path = BlobPath(root, descriptor);
        if (!File.Exists(path))
        {
            throw new ReleaseToolException($"The image archive does not hold {what}, the blob {descriptor.Digest}.");
        }
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.Length != descriptor.Size || Convert.ToHexStringLower(SHA256.HashData(bytes)) != descriptor.Hex)
        {
            throw new ReleaseToolException($"In the image archive, {what} is not the blob {descriptor.Digest} its descriptor names.");
        }
        return bytes;
    }

    private static async Task VerifyBlobAsync(string root, Descriptor descriptor, string what, CancellationToken cancellationToken)
    {
        var path = BlobPath(root, descriptor);
        if (!File.Exists(path))
        {
            throw new ReleaseToolException($"The image archive does not hold {what}, the blob {descriptor.Digest}.");
        }
        await using var stream = File.OpenRead(path);
        var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (stream.Length != descriptor.Size || sha256 != descriptor.Hex)
        {
            throw new ReleaseToolException($"In the image archive, {what} is not the blob {descriptor.Digest} its descriptor names.");
        }
    }

    private static string BlobPath(string root, Descriptor descriptor) => Path.Combine(root, "blobs", "sha256", descriptor.Hex);

    private static Descriptor Descriptor(JsonElement element, string what)
    {
        var digest = element.ValueKind == JsonValueKind.Object ? Text(element, "digest") : null;
        if (digest is null || digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            !digest[7..].All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !element.TryGetProperty("size", out var size) || !size.TryGetInt64(out var length) || length < 0 ||
            Text(element, "mediaType") is not { } mediaType)
        {
            throw new ReleaseToolException($"In the image archive, {what} has no sha256 digest, size and media type.");
        }
        return new Descriptor(mediaType, digest, length);
    }

    /// <summary>
    /// An image's state contract (install contract section 6, decision D1), from its <c>io.hvo.skymonitor.state-contracts</c>
    /// label: a JSON object naming each store the component keeps, in lower camel case, with the schema it writes and the
    /// schemas it reads, <c>{"writes": W, "reads": {"min": A, "max": B}}</c>, whole numbers with
    /// <c>1 &lt;= A &lt;= W &lt;= B</c>. release.json's <c>stateContracts</c> repeats it, so the two cannot differ.
    /// </summary>
    private static JsonObject ReadStateContracts(string? label, string platform)
    {
        ReleaseToolException Refusal() => new(
            $"The {platform} image's {StateContractsLabel} label is not a state contract: a JSON object of stores, each " +
            "{\"writes\": W, \"reads\": {\"min\": A, \"max\": B}} with whole numbers 1 <= A <= W <= B.");
        if (label is null)
        {
            throw Refusal();
        }
        try
        {
            using var document = JsonDocument.Parse(label, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Refusal();
            }
            foreach (var store in document.RootElement.EnumerateObject())
            {
                if (!IsStoreName(store.Name) || !HasExactly(store.Value, StoreMembers) ||
                    !HasExactly(store.Value.GetProperty("reads"), ReadsMembers) ||
                    Schema(store.Value, "writes") is not { } writes || Schema(store.Value.GetProperty("reads"), "min") is not { } min ||
                    Schema(store.Value.GetProperty("reads"), "max") is not { } max || min > writes || writes > max)
                {
                    throw Refusal();
                }
            }
            return JsonNode.Parse(label)!.AsObject();
        }
        catch (JsonException)
        {
            throw Refusal();
        }

        static bool IsStoreName(string name)
            => name.Length is > 0 and <= 64 && char.IsAsciiLetterLower(name[0]) && name.All(char.IsAsciiLetterOrDigit);

        static bool HasExactly(JsonElement element, string[] members)
            => element.ValueKind == JsonValueKind.Object &&
               element.EnumerateObject().Select(static member => member.Name).Order(StringComparer.Ordinal).SequenceEqual(members);

        static int? Schema(JsonElement element, string name)
            => element.GetProperty(name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var schema) && schema >= 1
                ? schema
                : null;
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Label(JsonElement labels, string name)
        => labels.ValueKind == JsonValueKind.Object ? Text(labels, name) : null;
}

/// <summary>An OCI content descriptor: a blob's media type, digest and size.</summary>
internal sealed record Descriptor(string MediaType, string Digest, long Size)
{
    public string Hex => Digest["sha256:".Length..];
}
