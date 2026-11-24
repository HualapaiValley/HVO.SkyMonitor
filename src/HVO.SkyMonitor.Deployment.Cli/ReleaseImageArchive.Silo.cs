using System.Text.Json;

namespace HVO.SkyMonitor.Deployment;

internal static partial class ReleaseImageArchive
{
    /// <summary>The largest staged Silo archive read; the pinned single-platform image is about 60 MB.</summary>
    internal const long MaximumSiloArchiveBytes = 1024L * 1024 * 1024;

    /// <summary>The artifact type of the provenance attestation Docker saves beside an image it pulled.</summary>
    internal const string AttestationArtifactType = "application/vnd.docker.attestation.manifest.v1+json";

    private const string AttestationSubjectAnnotation = "io.containerd.manifest.subject";

    /// <summary>
    /// Verifies an operator-staged archive of the pinned stock Silo image (#1966) before anything loads it: the archive
    /// a connected host using Docker's containerd image store writes with <c>docker save --platform linux/ARCH</c> of the
    /// pinned reference. Every blob must hash to its name. The layout's index names the pinned platform manifest and
    /// nothing else but provenance attestations of it, and tags nothing but the pinned release. <c>manifest.json</c>,
    /// which Docker's classic store loads instead, describes the same config and layers and tags nothing else. The
    /// manifest names only the pinned config and layers the archive holds at their declared sizes, the config is the pinned
    /// release for this OS and architecture, and the archive holds no blob none of these use. Throws a refusal naming the
    /// first disagreement, so an inconsistent archive is rejected before anything is loaded.
    /// </summary>
    public static async Task VerifySiloArchiveAsync(string path, SiloImagePin.PlatformImage platform, CancellationToken cancellationToken)
    {
        await using var stream = SafeFileSystem.OpenRegularFileRead(path);
        await VerifySiloArchiveAsync(stream, Path.GetFileName(path), platform, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies the archive <paramref name="stream"/> holds, read from its start (it must be seekable); see the path
    /// overload. <see cref="SiloImageSnapshot"/> verifies its private copy this way, so the bytes verified are the bytes
    /// Docker loads.
    /// </summary>
    public static async Task VerifySiloArchiveAsync(
        Stream stream, string name, SiloImagePin.PlatformImage platform, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(platform);
        var manifestBlob = BlobPrefix + Hex(platform.ManifestDigest);
        var configBlob = BlobPrefix + Hex(platform.ConfigDigest);
        var (entries, blobs) = await ReadSiloArchiveAsync(stream, name, [manifestBlob, configBlob], cancellationToken).ConfigureAwait(false);

        if (!entries.TryGetValue("oci-layout", out var layoutBytes))
            throw SiloRefusal(name, "it has no oci-layout");
        using (var layout = ParseSilo(layoutBytes, name, "oci-layout"))
        {
            OnlyFields(layout.RootElement, "oci-layout", reason => SiloRefusal(name, reason), "imageLayoutVersion");
            if (Text(layout.RootElement, "imageLayoutVersion") != "1.0.0")
                throw SiloRefusal(name, "its oci-layout is not version 1.0.0");
        }
        if (!blobs.TryGetValue(manifestBlob, out var manifestContent) || !entries.TryGetValue(manifestBlob, out var manifestBytes))
            throw SiloRefusal(name, $"it does not hold the pinned {platform.Platform} manifest");

        // The index: the pinned platform manifest once, and otherwise only attestations whose subject it is.
        var attestations = new List<string>();
        if (!entries.TryGetValue("index.json", out var indexBytes))
            throw SiloRefusal(name, "it has no index.json");
        using (var index = ParseSilo(indexBytes, name, "index.json"))
        {
            var root = index.RootElement;
            OnlyFields(root, "index.json", reason => SiloRefusal(name, reason), "schemaVersion", "mediaType", "manifests");
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("manifests", out var manifests) ||
                manifests.ValueKind != JsonValueKind.Array)
                throw SiloRefusal(name, "its index.json lists no manifests");
            var pinned = 0;
            foreach (var descriptor in manifests.EnumerateArray())
            {
                var digest = descriptor.ValueKind == JsonValueKind.Object ? Text(descriptor, "digest") : null;
                if (digest == platform.ManifestDigest)
                {
                    OnlyFields(descriptor, "index entry for the pinned manifest", reason => SiloRefusal(name, reason),
                        "mediaType", "digest", "size", "annotations", "platform");
                    if (!ManifestMediaTypes.Contains(Text(descriptor, "mediaType"), StringComparer.Ordinal) ||
                        Size(descriptor) != manifestContent.Length)
                        throw SiloRefusal(name, $"its index does not describe the pinned {platform.Platform} manifest as stored");
                    if (descriptor.TryGetProperty("platform", out var described) &&
                        (Text(described, "os") != "linux" || Text(described, "architecture") != platform.Architecture))
                        throw SiloRefusal(name, $"its index names the pinned manifest for a platform other than {platform.Platform}");
                    if (!PinnedNamesOnly(descriptor))
                        throw SiloRefusal(name, $"its index names the image as something other than the pinned release {SiloImagePin.Release}");
                    pinned++;
                }
                else if (digest is not null && IsDigest(digest) && IsAttestationOfPinned(descriptor, platform))
                {
                    if (!blobs.TryGetValue(BlobPrefix + Hex(digest), out var attestation) || Size(descriptor) != attestation.Length)
                        throw SiloRefusal(name, "it does not hold an attestation its index names at its declared size");
                    attestations.Add(digest);
                }
                else
                {
                    throw SiloRefusal(name, $"its index names an image other than the pinned {platform.Platform} manifest");
                }
            }
            if (pinned != 1)
                throw SiloRefusal(name, $"its index does not name the pinned {platform.Platform} manifest exactly once");
        }

        // Attestation manifests are read in a second pass: their digests are known only from the index, which docker save
        // writes after the blobs.
        if (attestations.Count != 0)
        {
            (entries, blobs) = await ReadSiloArchiveAsync(stream, name,
                [manifestBlob, configBlob, .. attestations.Select(static digest => BlobPrefix + Hex(digest))], cancellationToken).ConfigureAwait(false);
        }

        var used = new HashSet<string>(StringComparer.Ordinal) { manifestBlob, configBlob };
        string[] layers;
        using (var manifest = ParseSilo(manifestBytes, name, "the platform manifest"))
        {
            var root = manifest.RootElement;
            if (!ManifestMediaTypes.Contains(Text(root, "mediaType"), StringComparer.Ordinal) ||
                Member(root, "config") is not { } config || Text(config, "digest") != platform.ConfigDigest)
                throw SiloRefusal(name, "its platform manifest does not name the pinned config");
            if (!blobs.TryGetValue(configBlob, out var configContent) || Size(config) != configContent.Length)
                throw SiloRefusal(name, "it does not hold the pinned config at its declared size");
            layers = UsedLayers(root, blobs, name, "the platform manifest");
            used.UnionWith(layers);
        }
        foreach (var digest in attestations)
        {
            var blob = BlobPrefix + Hex(digest);
            using var attestation = ParseSilo(entries[blob], name, "an attestation manifest");
            var root = attestation.RootElement;
            if (Text(root, "mediaType") != "application/vnd.oci.image.manifest.v1+json" ||
                Text(root, "artifactType") != AttestationArtifactType ||
                Member(root, "subject") is not { } subject || Text(subject, "digest") != platform.ManifestDigest ||
                Member(root, "config") is not { } config || Text(config, "digest") is not { } configDigest || !IsDigest(configDigest) ||
                !blobs.TryGetValue(BlobPrefix + Hex(configDigest), out var configContent) || Size(config) != configContent.Length)
                throw SiloRefusal(name, "an attestation its index names is not an attestation of the pinned manifest");
            used.Add(blob);
            used.Add(BlobPrefix + Hex(configDigest));
            used.UnionWith(UsedLayers(root, blobs, name, "an attestation manifest"));
        }

        // Docker's classic store loads the image manifest.json describes rather than the OCI index, so it must describe
        // the same image and tag nothing but the pinned release.
        if (!entries.TryGetValue("manifest.json", out var dockerManifestBytes))
            throw SiloRefusal(name, "it has no manifest.json");
        using (var dockerManifest = ParseSilo(dockerManifestBytes, name, "manifest.json"))
        {
            var only = dockerManifest.RootElement.ValueKind == JsonValueKind.Array && dockerManifest.RootElement.GetArrayLength() == 1
                ? dockerManifest.RootElement[0]
                : default;
            OnlyFields(only, "manifest.json", reason => SiloRefusal(name, reason), "Config", "RepoTags", "Layers");
            var describesTheImage = only.ValueKind == JsonValueKind.Object && Text(only, "Config") == configBlob &&
                                    only.TryGetProperty("Layers", out var dockerLayers) && dockerLayers.ValueKind == JsonValueKind.Array &&
                                    dockerLayers.EnumerateArray().Select(static layer => layer.ValueKind == JsonValueKind.String ? layer.GetString() : null)
                                        .SequenceEqual(layers, StringComparer.Ordinal);
            var tagsOnlyThePin = only.ValueKind == JsonValueKind.Object &&
                                 (!only.TryGetProperty("RepoTags", out var tags) || tags.ValueKind == JsonValueKind.Null ||
                                  (tags.ValueKind == JsonValueKind.Array && tags.EnumerateArray().All(static tag =>
                                      tag.ValueKind == JsonValueKind.String && PinnedTags.Contains(tag.GetString(), StringComparer.Ordinal))));
            if (!describesTheImage || !tagsOnlyThePin)
                throw SiloRefusal(name, "its manifest.json does not describe the pinned image alone, under no other name");
        }

        using (var configuration = ParseSilo(entries[configBlob], name, "the image config"))
        {
            var image = configuration.RootElement;
            var labels = Member(image, "config") is { } settings && Member(settings, "Labels") is { } values ? values : default;
            if (Text(image, "os") != "linux" || Text(image, "architecture") != platform.Architecture ||
                Text(labels, "org.opencontainers.image.version") != SiloImagePin.Release ||
                Text(labels, "org.opencontainers.image.revision") != SiloImagePin.SourceRevision ||
                Text(labels, "org.opencontainers.image.licenses") != SiloImagePin.License)
                throw SiloRefusal(name, $"its image is not the pinned Silo release for linux/{platform.Architecture}");
        }

        if (blobs.Keys.FirstOrDefault(blob => !used.Contains(blob)) is { } unused)
            throw SiloRefusal(name, $"it holds {Printable(unused)}, which the pinned image does not use");
    }

    /// <summary>The only names a staged archive may give the pinned image: its release tag in the pinned repository.</summary>
    private static readonly string[] PinnedTags =
    [
        $"{SiloImagePin.Repository}:{SiloImagePin.Release}",
        $"{SiloImagePin.Repository["docker.io/".Length..]}:{SiloImagePin.Release}"
    ];

    private static async Task<(Dictionary<string, byte[]> Entries, Dictionary<string, BlobContent> Blobs)> ReadSiloArchiveAsync(
        Stream stream, string name, HashSet<string> wanted, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        if (stream.Length > MaximumSiloArchiveBytes)
            throw InstallerException.Refused($"The staged Silo archive {name} is larger than any image of the pinned release.");
        Dictionary<string, byte[]> entries;
        Dictionary<string, BlobContent> blobs;
        try
        {
            (entries, blobs) = await ReadEntriesAsync(stream, name, wanted, maximumEntries: 4096, cancellationToken).ConfigureAwait(false);
        }
        catch (InstallerException exception) when (!exception.IsRefusal)
        {
            throw InstallerException.Refused(exception.Message, exception);
        }
        foreach (var (blobName, content) in blobs)
        {
            if (content.Digest != "sha256:" + blobName[BlobPrefix.Length..])
                throw SiloRefusal(name, $"its blob {Printable(blobName)} does not hash to its name");
        }
        return (entries, blobs);
    }

    /// <summary>The blobs a manifest's layers name, each held by the archive at its declared size.</summary>
    private static string[] UsedLayers(JsonElement manifest, Dictionary<string, BlobContent> blobs, string archive, string what)
    {
        if (!manifest.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array || layers.GetArrayLength() == 0)
            throw SiloRefusal(archive, $"{what} lists no layers");
        var names = new List<string>();
        foreach (var layer in layers.EnumerateArray())
        {
            var digest = layer.ValueKind == JsonValueKind.Object ? Text(layer, "digest") : null;
            if (digest is null || !IsDigest(digest) || !blobs.TryGetValue(BlobPrefix + Hex(digest), out var content) ||
                Size(layer) != content.Length)
                throw SiloRefusal(archive, $"a layer of {what} is missing or not at its declared size");
            names.Add(BlobPrefix + Hex(digest));
        }
        return [.. names];
    }

    /// <summary>
    /// Whether an index entry gives the pinned manifest no name but the pinned release: Docker records the repository it
    /// came from, and a save by tag adds that tag, which a load applies.
    /// </summary>
    private static bool PinnedNamesOnly(JsonElement descriptor)
    {
        if (!descriptor.TryGetProperty("annotations", out var annotations))
            return true;
        if (annotations.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var annotation in annotations.EnumerateObject())
        {
            var value = annotation.Value.ValueKind == JsonValueKind.String ? annotation.Value.GetString() : null;
            var allowed = annotation.Name switch
            {
                "containerd.io/distribution.source.docker.io" => value == SiloImagePin.Repository["docker.io/".Length..],
                "io.containerd.image.name" => value == PinnedTags[0],
                "org.opencontainers.image.ref.name" => value == SiloImagePin.Release,
                _ => false
            };
            if (!allowed)
                return false;
        }
        return true;
    }

    /// <summary>Whether an index entry is a provenance attestation of the pinned manifest, which a load never names.</summary>
    private static bool IsAttestationOfPinned(JsonElement descriptor, SiloImagePin.PlatformImage platform)
    {
        if (descriptor.ValueKind != JsonValueKind.Object || Text(descriptor, "artifactType") != AttestationArtifactType ||
            Text(descriptor, "mediaType") != "application/vnd.oci.image.manifest.v1+json" ||
            Member(descriptor, "annotations") is not { ValueKind: JsonValueKind.Object } annotations)
            return false;
        var properties = annotations.EnumerateObject().ToArray();
        return properties.Length == 1 && properties[0].Name == AttestationSubjectAnnotation &&
               properties[0].Value.ValueKind == JsonValueKind.String && properties[0].Value.GetString() == platform.ManifestDigest &&
               descriptor.EnumerateObject().All(static property =>
                   property.Name is "mediaType" or "digest" or "size" or "annotations" or "artifactType");
    }

    /// <summary>The object <paramref name="name"/> names, or <see langword="null"/> when it names none.</summary>
    private static JsonElement? Member(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static JsonDocument ParseSilo(byte[] bytes, string archive, string what)
    {
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException exception)
        {
            throw InstallerException.Refused($"The staged Silo archive {archive} is not usable: {what} is not JSON.", exception);
        }
    }

    private static InstallerException SiloRefusal(string archive, string reason)
        => InstallerException.Refused($"The staged Silo archive {archive} is not the pinned Silo image: {reason}. Nothing was loaded.");
}
