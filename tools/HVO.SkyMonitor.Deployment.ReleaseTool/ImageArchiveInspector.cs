using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.SkyMonitor.Deployment.ReleaseTool;

/// <summary>
/// The identity a single-platform OCI image archive actually carries, derived from the archive bytes.
/// </summary>
internal sealed record ImageArchiveIdentity(
    string ManifestDigest,
    string ImageId,
    string OperatingSystem,
    string Architecture,
    IReadOnlyDictionary<string, string> Labels);

/// <summary>
/// Reads the OCI layout inside a <c>docker buildx --output type=docker</c> or <c>docker image save</c> archive and
/// returns the image manifest digest, immutable image ID, platform, and labels without a Docker daemon.
/// Every metadata blob is content-addressed, so a rewritten manifest, configuration, or label set is rejected here
/// rather than being copied into signed release metadata on the strength of a caller-supplied assertion.
/// </summary>
internal static class ImageArchiveInspector
{
    private const long MaximumMetadataBlobBytes = 1024 * 1024;
    private const long MaximumMetadataTotalBytes = 16 * 1024 * 1024;
    private const int MaximumIndexDepth = 2;
    private const string BlobPrefix = "blobs/sha256/";

    private static readonly string[] ManifestMediaTypes =
    [
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json"
    ];

    private static readonly string[] IndexMediaTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json"
    ];

    /// <summary>The byte length of a metadata blob inside the archive, as an OCI descriptor records it.</summary>
    public static int MeasureManifest(string archivePath, string digest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var (index, blobs) = ReadArchive(archivePath);
        index.Dispose();
        return blobs.TryGetValue(digest, out var bytes)
            ? bytes.Length
            : throw new ReleaseToolException($"The image archive omits the manifest blob '{digest}'.");
    }

    public static ImageArchiveIdentity Inspect(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var (index, blobs) = ReadArchive(archivePath);
        var manifestDigest = ResolveSingleManifest(index, blobs, MaximumIndexDepth);
        using var manifest = Parse(blobs, manifestDigest, "image manifest");
        if (!manifest.RootElement.TryGetProperty("config", out var config) ||
            !config.TryGetProperty("digest", out var configDigestValue) ||
            configDigestValue.GetString() is not { } configDigest)
        {
            throw new ReleaseToolException("The image archive manifest does not identify a configuration blob.");
        }
        using var configuration = Parse(blobs, configDigest, "image configuration");
        var root = configuration.RootElement;
        var operatingSystem = root.TryGetProperty("os", out var osValue) ? osValue.GetString() : null;
        var architecture = root.TryGetProperty("architecture", out var archValue) ? archValue.GetString() : null;
        if (string.IsNullOrEmpty(operatingSystem) || string.IsNullOrEmpty(architecture))
        {
            throw new ReleaseToolException("The image archive configuration omits its platform identity.");
        }
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("config", out var runtimeConfig) &&
            runtimeConfig.TryGetProperty("Labels", out var labelValues) &&
            labelValues.ValueKind == JsonValueKind.Object)
        {
            foreach (var label in labelValues.EnumerateObject())
            {
                if (label.Value.ValueKind == JsonValueKind.String)
                {
                    labels[label.Name] = label.Value.GetString()!;
                }
            }
        }
        return new ImageArchiveIdentity(manifestDigest, configDigest, operatingSystem, architecture, labels);
    }

    private static (JsonDocument Index, Dictionary<string, byte[]> Blobs) ReadArchive(string archivePath)
    {
        byte[]? indexBytes = null;
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long metadataBytes = 0;
        using var file = File.OpenRead(archivePath);
        using var source = OpenArchiveStream(file);
        using var reader = new TarReader(source);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
            {
                continue;
            }
            var name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
            if (name == "index.json")
            {
                indexBytes = ReadEntry(entry, MaximumMetadataBlobBytes);
                continue;
            }
            if (!name.StartsWith(BlobPrefix, StringComparison.Ordinal) || entry.Length > MaximumMetadataBlobBytes)
            {
                continue;
            }
            var digestHex = name[BlobPrefix.Length..];
            if (digestHex.Length != 64 || !digestHex.All(static value => value is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                continue;
            }
            metadataBytes = checked(metadataBytes + entry.Length);
            if (metadataBytes > MaximumMetadataTotalBytes)
            {
                throw new ReleaseToolException("The image archive contains more small blobs than a release candidate may carry.");
            }
            var content = ReadEntry(entry, MaximumMetadataBlobBytes);
            if (Convert.ToHexStringLower(SHA256.HashData(content)) != digestHex)
            {
                throw new ReleaseToolException($"Image archive blob 'sha256:{digestHex}' does not match its content address.");
            }
            blobs[$"sha256:{digestHex}"] = content;
        }
        if (indexBytes is null)
        {
            throw new ReleaseToolException(
                "The image archive has no OCI index.json. Build it with 'docker buildx build --output type=docker,dest=<file>'.");
        }
        return (Parse(indexBytes, "image index"), blobs);
    }

    private static Stream OpenArchiveStream(FileStream file)
    {
        Span<byte> magic = stackalloc byte[2];
        if (file.Read(magic) != magic.Length)
        {
            throw new ReleaseToolException("The image archive is too small to be an archive.");
        }
        file.Position = 0;
        return magic[0] == 0x1F && magic[1] == 0x8B
            ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: true)
            : file;
    }

    private static byte[] ReadEntry(TarEntry entry, long maximumBytes)
    {
        if (entry.Length > maximumBytes)
        {
            throw new ReleaseToolException($"Image archive entry '{entry.Name}' exceeds its bounded metadata size.");
        }
        using var buffer = new MemoryStream(checked((int)entry.Length));
        entry.DataStream!.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string ResolveSingleManifest(JsonDocument index, Dictionary<string, byte[]> blobs, int depth)
    {
        using (index)
        {
            var manifests = ReadDescriptors(index.RootElement);
            var leaves = new List<string>();
            foreach (var (mediaType, digest) in manifests)
            {
                if (ManifestMediaTypes.Contains(mediaType, StringComparer.Ordinal))
                {
                    leaves.Add(digest);
                }
                else if (IndexMediaTypes.Contains(mediaType, StringComparer.Ordinal))
                {
                    if (depth <= 1)
                    {
                        throw new ReleaseToolException("The image archive nests indexes more deeply than a release archive may.");
                    }
                    leaves.Add(ResolveSingleManifest(Parse(blobs, digest, "nested image index"), blobs, depth - 1));
                }
            }
            var distinct = leaves.Distinct(StringComparer.Ordinal).ToArray();
            return distinct.Length == 1
                ? distinct[0]
                : throw new ReleaseToolException(
                    $"The image archive must contain exactly one platform manifest but contains {distinct.Length}.");
        }
    }

    private static List<(string MediaType, string Digest)> ReadDescriptors(JsonElement root)
    {
        if (!root.TryGetProperty("manifests", out var manifests) || manifests.ValueKind != JsonValueKind.Array)
        {
            throw new ReleaseToolException("The image archive index does not list any manifests.");
        }
        var result = new List<(string, string)>();
        foreach (var descriptor in manifests.EnumerateArray())
        {
            if (!descriptor.TryGetProperty("mediaType", out var mediaType) || mediaType.GetString() is not { } mediaTypeValue ||
                !descriptor.TryGetProperty("digest", out var digest) || digest.GetString() is not { } digestValue)
            {
                throw new ReleaseToolException("The image archive index contains an incomplete descriptor.");
            }
            result.Add((mediaTypeValue, digestValue));
        }
        return result;
    }

    private static JsonDocument Parse(Dictionary<string, byte[]> blobs, string digest, string description)
        => blobs.TryGetValue(digest, out var bytes)
            ? Parse(bytes, description)
            : throw new ReleaseToolException($"The image archive omits the {description} blob '{digest}'.");

    private static JsonDocument Parse(byte[] bytes, string description)
    {
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        }
        catch (JsonException exception)
        {
            throw new ReleaseToolException($"The image archive {description} is not valid JSON.", exception);
        }
    }
}
