using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// A release's folder as an operator receives it: release.json, SHA256SUMS that covers it, an offline archive per
/// platform and an installer. Each archive is a minimal OCI image archive in the release tool's layout: the release's
/// image index, the platform's manifest and configuration, one layer, and Docker's manifest.json, so the installer's
/// own read of the archive sees the release's image unless a test edits it (<c>editArchive</c>) or labels it otherwise
/// (<c>labels</c>). Every digest release.json names is the digest of those bytes. Tests change release.json through
/// <see cref="Rewrite"/>, which keeps SHA256SUMS consistent, to reach the reader's own checks, or write either file
/// directly to reach the cross-checks.
/// </summary>
internal sealed class ReleaseFixture : IDisposable
{
    public const string Commit = "0123456789abcdef0123456789abcdef01234567";

    public const string IndexMediaType = "application/vnd.oci.image.index.v1+json";

    public const string ManifestMediaType = "application/vnd.oci.image.manifest.v1+json";

    /// <summary>The labels a fake Docker daemon reports for a CameraAgent image, without the release's version and commit.</summary>
    public static readonly IReadOnlyDictionary<string, string> ContractLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["io.hvo.skymonitor.state-compatibility"] = "cameraagent-state-v2",
        ["io.hvo.skymonitor.minimum-compatible-revision"] = new string('7', 40),
        ["io.hvo.skymonitor.identity-migration"] = "20260827053715_InitialIdentity",
        ["io.hvo.skymonitor.raw-ingress-schema"] = "13",
        ["io.hvo.skymonitor.catalog-manifest-version"] = "2",
        ["io.hvo.skymonitor.component"] = "CameraAgent",
        ["io.hvo.skymonitor.configuration-contract"] = "cameraagent-install-v1",
        ["io.hvo.skymonitor.catalog-contract"] = "hyg-v42-production-p3-s2",
        ["io.hvo.skymonitor.replay-runner-contract"] = "local-replay-runner-v1"
    };

    private readonly bool ownsRoot;

    private ReleaseFixture(string folder, bool ownsRoot, string version, JsonObject manifest)
    {
        Folder = folder;
        this.ownsRoot = ownsRoot;
        Version = version;
        Manifest = manifest;
    }

    /// <summary>The release's folder, which <c>--release</c> names.</summary>
    public string Folder { get; }

    public string Version { get; }

    /// <summary>The release.json last written, which <see cref="Rewrite"/> changes.</summary>
    public JsonObject Manifest { get; private set; }

    /// <summary>The digest of the image the release names: its index over the platforms, or one platform's manifest.</summary>
    public string Digest => (string)Manifest["images"]![ReleaseManifest.CameraAgentImageName]!["digest"]!;

    public string Repository => (string)Manifest["images"]![ReleaseManifest.CameraAgentImageName]!["repository"]!;

    public string DigestReference => $"{Repository}@{Digest}";

    /// <summary>
    /// Writes a release of <paramref name="version"/> under <paramref name="root"/>, publishing the image for
    /// <paramref name="platforms"/> (both by default) with an offline archive for each of
    /// <paramref name="archivePlatforms"/> (the same by default). The image carries <paramref name="labels"/>, by default
    /// <see cref="ReleaseLabels"/>. <paramref name="singleManifest"/> names one platform's manifest as the release's image,
    /// as a one-platform build without an index is. <paramref name="editArchive"/> changes a platform's archive entries
    /// after their digests were taken, before the archive is written and release.json records its size and SHA-256, so the
    /// release stays consistent with its files while its archive disagrees with what it names. <paramref name="config"/>
    /// rewrites a platform's configuration before its digest is taken, so release.json names the rewritten bytes.
    /// </summary>
    public static ReleaseFixture Create(
        string? root = null,
        string version = "1.2.0-dev.3",
        IReadOnlyList<string>? platforms = null,
        IReadOnlyList<string>? archivePlatforms = null,
        IReadOnlyList<(string Version, string Text)>? upgradeNotes = null,
        IReadOnlyDictionary<string, string>? labels = null,
        bool singleManifest = false,
        Action<string, List<ArchiveEntry>>? editArchive = null,
        Func<string, string, string>? config = null)
    {
        var ownsRoot = root is null;
        root ??= Path.Combine(Path.GetTempPath(), $"hvo-release-{Guid.NewGuid():N}");
        var folder = Path.Combine(root, $"release-v{version}");
        Directory.CreateDirectory(folder);
        platforms ??= ReleaseManifest.ImagePlatforms;
        archivePlatforms ??= platforms;
        labels ??= ReleaseLabels(version);
        if (singleManifest && platforms.Count != 1)
        {
            throw new ArgumentException("A release without an index publishes one platform.", nameof(singleManifest));
        }
        var repository = $"ghcr.io/hualapaivalley/{ReleaseManifest.CameraAgentImageName}";

        // Each platform's image: one layer, a configuration labelled as the release, and the manifest over them.
        var images = new Dictionary<string, (byte[] Layer, byte[] Config, byte[] Manifest)>(StringComparer.Ordinal);
        var indexEntries = new JsonArray();
        foreach (var platform in platforms)
        {
            var architecture = platform["linux/".Length..];
            var layer = Encoding.UTF8.GetBytes($"layer {version} {platform}");
            var configuration = new JsonObject
            {
                ["architecture"] = architecture,
                ["os"] = "linux",
                ["config"] = new JsonObject
                {
                    ["Labels"] = new JsonObject([.. labels.Select(static label =>
                    KeyValuePair.Create(label.Key, (JsonNode?)label.Value))])
                }
            }.ToJsonString();
            var configBytes = Encoding.UTF8.GetBytes(config?.Invoke(platform, configuration) ?? configuration);
            var manifest = Bytes(new JsonObject
            {
                ["schemaVersion"] = 2,
                ["mediaType"] = ManifestMediaType,
                ["config"] = Descriptor("application/vnd.oci.image.config.v1+json", configBytes),
                ["layers"] = new JsonArray(Descriptor("application/vnd.oci.image.layer.v1.tar+gzip", layer))
            });
            images[platform] = (layer, configBytes, manifest);
            var entry = Descriptor(ManifestMediaType, manifest);
            entry["platform"] = new JsonObject { ["architecture"] = architecture, ["os"] = "linux" };
            indexEntries.Add(entry);
        }
        var index = Bytes(new JsonObject { ["schemaVersion"] = 2, ["mediaType"] = IndexMediaType, ["manifests"] = indexEntries });
        var digest = singleManifest ? DigestOf(images[platforms[0]].Manifest) : DigestOf(index);

        var assets = new JsonArray();
        var archives = new JsonArray();
        foreach (var platform in archivePlatforms)
        {
            var (layer, configBlob, manifest) = images[platform];
            var name = OfflineArchive.NameFor(version, platform);
            var top = singleManifest ? Descriptor(ManifestMediaType, manifest) : Descriptor(IndexMediaType, index);
            top["annotations"] = new JsonObject
            {
                ["io.containerd.image.name"] = $"{repository}:{version}",
                ["org.opencontainers.image.ref.name"] = version
            };
            var entries = new List<ArchiveEntry>
            {
                new("oci-layout", """{"imageLayoutVersion":"1.0.0"}"""u8.ToArray()),
                new("index.json", Bytes(new JsonObject { ["schemaVersion"] = 2, ["manifests"] = new JsonArray(top) })),
                new("manifest.json", Bytes(new JsonArray(new JsonObject
                {
                    ["Config"] = BlobName(configBlob),
                    ["RepoTags"] = new JsonArray($"{repository}:{version}"),
                    ["Layers"] = new JsonArray(BlobName(layer))
                })))
            };
            if (!singleManifest)
            {
                entries.Add(new(BlobName(index), index));
            }
            entries.AddRange([new(BlobName(manifest), manifest), new(BlobName(configBlob), configBlob), new(BlobName(layer), layer)]);
            editArchive?.Invoke(platform, entries);
            var bytes = Tar(entries);
            File.WriteAllBytes(Path.Combine(folder, name), bytes);
            archives.Add(new JsonObject
            {
                ["name"] = name,
                ["platform"] = platform,
                ["size"] = bytes.Length,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                ["manifestDigest"] = DigestOf(manifest),
                ["configDigest"] = DigestOf(configBlob)
            });
            assets.Add(Asset(name, ReleaseManifest.ImageArchiveKind, ReleaseManifest.RuntimeOf(platform), bytes));
        }
        var installer = Encoding.UTF8.GetBytes($"installer {version}");
        File.WriteAllBytes(Path.Combine(folder, "hvo-sky-install-linux-arm64"), installer);
        assets.Add(Asset("hvo-sky-install-linux-arm64", "installer", "linux-arm64", installer));
        var releaseJson = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["product"] = "HVO SkyMonitor",
            ["version"] = version,
            ["tag"] = $"v{version}",
            ["prerelease"] = version.Contains('-', StringComparison.Ordinal),
            ["commit"] = Commit,
            ["created"] = "2026-10-17T18:00:00Z",
            ["repository"] = ReleaseManifest.RepositoryUrl,
            ["images"] = new JsonObject
            {
                [ReleaseManifest.CameraAgentImageName] = new JsonObject
                {
                    ["repository"] = repository,
                    ["tag"] = version,
                    ["digest"] = digest,
                    ["reference"] = $"{repository}:{version}@{digest}",
                    ["platforms"] = new JsonArray([.. platforms.Select(static platform => (JsonNode)platform!)]),
                    ["offlineArchives"] = archives
                }
            },
            ["upgradeNotes"] = new JsonArray([.. (upgradeNotes ?? []).Select(static note =>
                (JsonNode)new JsonObject { ["version"] = note.Version, ["text"] = note.Text })]),
            ["assets"] = assets
        };
        var fixture = new ReleaseFixture(folder, ownsRoot, version, releaseJson);
        fixture.Rewrite(static _ => { });
        return fixture;
    }

    /// <summary>The labels a release's own image carries: the contract labels with the release's version and commit.</summary>
    public static Dictionary<string, string> ReleaseLabels(string version)
        => new(ContractLabels, StringComparer.Ordinal)
        {
            ["org.opencontainers.image.version"] = version,
            ["org.opencontainers.image.revision"] = Commit
        };

    /// <summary>The name an archive gives the blob of <paramref name="bytes"/>.</summary>
    public static string BlobName(byte[] bytes) => $"blobs/sha256/{DigestOf(bytes)["sha256:".Length..]}";

    public static string DigestOf(byte[] bytes) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    /// <summary>The offline archive release.json names for <paramref name="platform"/>.</summary>
    public JsonObject Archive(string platform)
        => Manifest["images"]![ReleaseManifest.CameraAgentImageName]!["offlineArchives"]!.AsArray()
            .Select(static node => node!.AsObject())
            .Single(node => (string)node["platform"]! == platform);

    public string ArchivePath(string platform) => Path.Combine(Folder, (string)Archive(platform)["name"]!);

    public string ConfigDigest(string platform) => (string)Archive(platform)["configDigest"]!;

    public string ManifestDigest(string platform) => (string)Archive(platform)["manifestDigest"]!;

    /// <summary>The labels the release's own image carries: the contract labels with the release's version and commit.</summary>
    public Dictionary<string, string> Labels() => ReleaseLabels(Version);

    /// <summary>Changes release.json and writes it with a SHA256SUMS that matches it and every asset it lists.</summary>
    public void Rewrite(Action<JsonObject> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var manifest = Manifest.DeepClone().AsObject();
        change(manifest);
        Manifest = manifest;
        var bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.WriteAllBytes(Path.Combine(Folder, ReleaseManifest.FileName), bytes);
        var sums = new StringBuilder();
        foreach (var asset in manifest["assets"]!.AsArray().Select(static node => node!.AsObject()))
        {
            sums.Append(CultureInfo(), $"{(string)asset["sha256"]!}  {(string)asset["name"]!}\n");
        }
        sums.Append(CultureInfo(), $"{Convert.ToHexStringLower(SHA256.HashData(bytes))}  {ReleaseManifest.FileName}\n");
        WriteChecksums(sums.ToString());
    }

    /// <summary>
    /// Changes release.json as text, which a JSON object cannot always hold (a property named twice), and writes the
    /// SHA256SUMS line for it to match.
    /// </summary>
    public void RewriteText(Func<string, string> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var path = Path.Combine(Folder, ReleaseManifest.FileName);
        var bytes = Encoding.UTF8.GetBytes(change(File.ReadAllText(path)));
        File.WriteAllBytes(path, bytes);
        var line = $"{Convert.ToHexStringLower(SHA256.HashData(bytes))}  {ReleaseManifest.FileName}";
        WriteChecksums(string.Concat(ReadChecksums().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(existing => (existing.EndsWith($"  {ReleaseManifest.FileName}", StringComparison.Ordinal) ? line : existing) + "\n")));
    }

    public void WriteChecksums(string text)
        => File.WriteAllText(Path.Combine(Folder, ReleaseChecksums.FileName), text, new UTF8Encoding(false));

    public string ReadChecksums() => File.ReadAllText(Path.Combine(Folder, ReleaseChecksums.FileName));

    public void Dispose()
    {
        if (Directory.Exists(Folder))
        {
            Directory.Delete(ownsRoot ? Path.GetDirectoryName(Folder)! : Folder, recursive: true);
        }
    }

    internal static string Digest256(string seed) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))}";

    private static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    private static JsonObject Descriptor(string mediaType, byte[] bytes) => new()
    {
        ["mediaType"] = mediaType,
        ["digest"] = DigestOf(bytes),
        ["size"] = bytes.Length
    };

    private static byte[] Tar(IEnumerable<ArchiveEntry> entries)
    {
        using var buffer = new MemoryStream();
        using (var tar = new TarWriter(buffer, TarEntryFormat.Ustar, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                var written = new UstarTarEntry(entry.Type, entry.Name);
                if (entry.Type == TarEntryType.RegularFile)
                {
                    written.DataStream = new MemoryStream(entry.Bytes, writable: false);
                }
                else if (entry.Type is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                {
                    written.LinkName = Encoding.UTF8.GetString(entry.Bytes);
                }
                tar.WriteEntry(written);
            }
        }
        return buffer.ToArray();
    }

    private static JsonObject Asset(string name, string kind, string? platform, byte[] bytes) => new()
    {
        ["name"] = name,
        ["kind"] = kind,
        ["platform"] = platform,
        ["size"] = bytes.Length,
        ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes))
    };

    private static System.Globalization.CultureInfo CultureInfo() => System.Globalization.CultureInfo.InvariantCulture;
}

/// <summary>One entry of an offline archive the fixture writes: a regular file's bytes, or a link's target.</summary>
internal sealed record ArchiveEntry(string Name, byte[] Bytes, TarEntryType Type = TarEntryType.RegularFile);
