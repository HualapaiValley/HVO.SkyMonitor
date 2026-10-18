using System.Buffers.Binary;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace HVO.SkyMonitor.Deployment.Distribution.Tests;

/// <summary>
/// The inputs of <c>create-release</c>: a multi-platform OCI image archive shaped as <c>docker buildx build --output
/// type=oci</c> writes it, with small deterministic layers and configurations labelled as the release's image, two
/// installers with the right ELF machine, and a folder of upgrade notes. Tests change the archive's parts through
/// <see cref="Build"/> to reach the splitter's refusals.
/// </summary>
internal sealed class OciReleaseFixture : IDisposable
{
    public const string Version = "1.2.0-dev.3";
    public const string Commit = "0123456789abcdef0123456789abcdef01234567";
    public const string Created = "2026-10-17T18:00:00Z";

    /// <summary>The image's <c>io.hvo.skymonitor.state-contracts</c> label, which release.json's <c>stateContracts</c> repeats.</summary>
    public const string StateContracts =
        """{"identity":{"writes":1,"reads":{"min":1,"max":1}},"rawIngress":{"writes":15,"reads":{"min":13,"max":15}}}""";
    public const string IndexMediaType = "application/vnd.oci.image.index.v1+json";
    public const string ManifestMediaType = "application/vnd.oci.image.manifest.v1+json";

    private OciReleaseFixture(string root)
    {
        Root = root;
        X64 = WriteElf("x64", 0x3e);
        Arm64 = WriteElf("arm64", 0xb7);
        Notes = Path.Combine(root, "upgrade-notes");
        Directory.CreateDirectory(Notes);
    }

    public string Root { get; }
    public string X64 { get; }
    public string Arm64 { get; }
    public string Notes { get; }
    public string ImageArchive => Path.Combine(Root, "image.oci.tar");

    /// <summary>The digests of each platform's manifest and configuration in the archive last built.</summary>
    public Dictionary<string, (string Manifest, string Config, string Layer)> Platforms { get; } = new(StringComparer.Ordinal);

    public string IndexDigest { get; private set; } = string.Empty;

    public static OciReleaseFixture Create()
    {
        var fixture = new OciReleaseFixture(Directory.CreateTempSubdirectory("hvo-release-tool-").FullName);
        fixture.Build();
        return fixture;
    }

    /// <summary>
    /// Writes the image archive for <paramref name="platforms"/> (both by default). <paramref name="labels"/> changes a
    /// platform's labels; <paramref name="layout"/> changes the archive's index.json; <paramref name="tamper"/> rewrites a
    /// blob's bytes, by name, after its digest was taken. <paramref name="singleManifest"/> writes one platform's manifest
    /// with no index over it, as buildx writes a one-platform build without provenance.
    /// </summary>
    public void Build(
        IReadOnlyList<string>? platforms = null,
        Action<string, JsonObject>? labels = null,
        Action<JsonObject>? layout = null,
        Func<string, byte[], byte[]>? tamper = null,
        bool singleManifest = false)
    {
        platforms ??= ["linux/amd64", "linux/arm64"];
        Platforms.Clear();
        var blobs = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        string Add(byte[] bytes)
        {
            var digest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
            blobs[digest] = bytes;
            return digest;
        }
        var entries = new JsonArray();
        foreach (var platform in platforms)
        {
            var (os, architecture) = (platform.Split('/')[0], platform.Split('/')[1]);
            var layer = Encoding.UTF8.GetBytes($"layer {platform}");
            var layerDigest = Add(layer);
            var imageLabels = new JsonObject
            {
                ["io.hvo.skymonitor.component"] = "CameraAgent",
                ["org.opencontainers.image.version"] = Version,
                ["org.opencontainers.image.revision"] = Commit,
                ["org.opencontainers.image.created"] = Created,
                ["io.hvo.skymonitor.state-contracts"] = StateContracts
            };
            labels?.Invoke(platform, imageLabels);
            var config = Bytes(new JsonObject
            {
                ["architecture"] = architecture,
                ["os"] = os,
                ["config"] = new JsonObject { ["Labels"] = imageLabels }
            });
            var configDigest = Add(config);
            var manifest = Bytes(new JsonObject
            {
                ["schemaVersion"] = 2,
                ["mediaType"] = ManifestMediaType,
                ["config"] = Descriptor("application/vnd.oci.image.config.v1+json", configDigest, config.Length),
                ["layers"] = new JsonArray(Descriptor("application/vnd.oci.image.layer.v1.tar+gzip", layerDigest, layer.Length))
            });
            var manifestDigest = Add(manifest);
            var entry = Descriptor(ManifestMediaType, manifestDigest, manifest.Length);
            entry["platform"] = new JsonObject { ["architecture"] = architecture, ["os"] = os };
            entries.Add(entry);
            Platforms[platform] = (manifestDigest, configDigest, layerDigest);
        }
        JsonObject top;
        if (singleManifest)
        {
            var manifestEntry = entries.Single()!.DeepClone().AsObject();
            manifestEntry.Remove("platform");
            IndexDigest = string.Empty;
            top = new JsonObject { ["schemaVersion"] = 2, ["manifests"] = new JsonArray(manifestEntry) };
        }
        else
        {
            var index = Bytes(new JsonObject { ["schemaVersion"] = 2, ["mediaType"] = IndexMediaType, ["manifests"] = entries });
            IndexDigest = Add(index);
            top = new JsonObject
            {
                ["schemaVersion"] = 2,
                ["manifests"] = new JsonArray(Descriptor(IndexMediaType, IndexDigest, index.Length))
            };
        }
        layout?.Invoke(top);
        if (File.Exists(ImageArchive))
        {
            File.Delete(ImageArchive);
        }
        using var file = File.Create(ImageArchive);
        using var tar = new TarWriter(file, TarEntryFormat.Pax, leaveOpen: true);
        Write(tar, "oci-layout", "{\"imageLayoutVersion\":\"1.0.0\"}"u8.ToArray());
        Write(tar, "index.json", Bytes(top));
        foreach (var (digest, bytes) in blobs)
        {
            var name = $"blobs/sha256/{digest[7..]}";
            Write(tar, name, tamper?.Invoke(name, bytes) ?? bytes);
        }
    }

    public string WriteNote(string version, string text)
    {
        var path = Path.Combine(Notes, $"{version}.md");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
        return path;
    }

    public string[] Arguments(string output, params string[] extra)
        =>
        [
            "create-release",
            "--version", Version,
            "--commit", Commit,
            "--created-utc", Created,
            "--image-archive", ImageArchive,
            "--installer-linux-x64", X64,
            "--installer-linux-arm64", Arm64,
            "--upgrade-notes", Notes,
            "--output", output,
            .. extra
        ];

    public void Dispose() => Directory.Delete(Root, recursive: true);

    private string WriteElf(string name, ushort machine)
    {
        var bytes = new byte[64];
        "\u007fELF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18, 2), machine);
        var path = Path.Combine(Root, $"installer-{name}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static JsonObject Descriptor(string mediaType, string digest, long size)
        => new() { ["mediaType"] = mediaType, ["digest"] = digest, ["size"] = size };

    private static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    private static void Write(TarWriter tar, string name, byte[] bytes)
        => tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(bytes),
            ModificationTime = DateTimeOffset.UnixEpoch
        });
}
