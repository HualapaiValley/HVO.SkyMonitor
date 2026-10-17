using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// A release's folder as an operator receives it: release.json, SHA256SUMS that covers it, an offline archive per
/// platform and an installer. The archives hold placeholder bytes; a fake Docker daemon stands in for loading them,
/// and reports the configuration digest each archive names as the image ID, as Docker's classic image store does.
/// Tests change release.json through <see cref="Rewrite"/>, which keeps SHA256SUMS consistent, to reach the reader's
/// own checks, or write either file directly to reach the cross-checks.
/// </summary>
internal sealed class ReleaseFixture : IDisposable
{
    public const string Commit = "0123456789abcdef0123456789abcdef01234567";

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

    /// <summary>The digest of the image index over the release's platforms.</summary>
    public string Digest => (string)Manifest["images"]![ReleaseManifest.CameraAgentImageName]!["digest"]!;

    public string Repository => (string)Manifest["images"]![ReleaseManifest.CameraAgentImageName]!["repository"]!;

    public string DigestReference => $"{Repository}@{Digest}";

    /// <summary>
    /// Writes a release of <paramref name="version"/> under <paramref name="root"/>, publishing the image for
    /// <paramref name="platforms"/> (both by default) with an offline archive for each of
    /// <paramref name="archivePlatforms"/> (the same by default). <paramref name="configDigests"/> sets the image ID
    /// a platform's archive names; each is otherwise derived from the platform.
    /// </summary>
    public static ReleaseFixture Create(
        string? root = null,
        string version = "1.2.0-dev.3",
        IReadOnlyList<string>? platforms = null,
        IReadOnlyList<string>? archivePlatforms = null,
        IReadOnlyDictionary<string, string>? configDigests = null,
        IReadOnlyList<(string Version, string Text)>? upgradeNotes = null)
    {
        var ownsRoot = root is null;
        root ??= Path.Combine(Path.GetTempPath(), $"hvo-release-{Guid.NewGuid():N}");
        var folder = Path.Combine(root, $"release-v{version}");
        Directory.CreateDirectory(folder);
        platforms ??= ReleaseManifest.ImagePlatforms;
        archivePlatforms ??= platforms;
        var digest = Digest256($"index {version}");
        var assets = new JsonArray();
        var archives = new JsonArray();
        foreach (var platform in archivePlatforms)
        {
            var name = OfflineArchive.NameFor(version, platform);
            var bytes = Encoding.UTF8.GetBytes($"offline archive {version} {platform}");
            File.WriteAllBytes(Path.Combine(folder, name), bytes);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            archives.Add(new JsonObject
            {
                ["name"] = name,
                ["platform"] = platform,
                ["size"] = bytes.Length,
                ["sha256"] = sha256,
                ["manifestDigest"] = Digest256($"manifest {version} {platform}"),
                ["configDigest"] = configDigests?.GetValueOrDefault(platform) ?? Digest256($"config {version} {platform}")
            });
            assets.Add(Asset(name, ReleaseManifest.ImageArchiveKind, ReleaseManifest.RuntimeOf(platform), bytes));
        }
        var installer = Encoding.UTF8.GetBytes($"installer {version}");
        File.WriteAllBytes(Path.Combine(folder, "hvo-sky-install-linux-arm64"), installer);
        assets.Add(Asset("hvo-sky-install-linux-arm64", "installer", "linux-arm64", installer));
        var repository = $"ghcr.io/hualapaivalley/{ReleaseManifest.CameraAgentImageName}";
        var manifest = new JsonObject
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
        var fixture = new ReleaseFixture(folder, ownsRoot, version, manifest);
        fixture.Rewrite(static _ => { });
        return fixture;
    }

    /// <summary>The offline archive release.json names for <paramref name="platform"/>.</summary>
    public JsonObject Archive(string platform)
        => Manifest["images"]![ReleaseManifest.CameraAgentImageName]!["offlineArchives"]!.AsArray()
            .Select(static node => node!.AsObject())
            .Single(node => (string)node["platform"]! == platform);

    public string ArchivePath(string platform) => Path.Combine(Folder, (string)Archive(platform)["name"]!);

    public string ConfigDigest(string platform) => (string)Archive(platform)["configDigest"]!;

    public string ManifestDigest(string platform) => (string)Archive(platform)["manifestDigest"]!;

    /// <summary>The labels the release's own image carries: the contract labels with the release's version and commit.</summary>
    public Dictionary<string, string> Labels()
        => new(ContractLabels, StringComparer.Ordinal)
        {
            ["org.opencontainers.image.version"] = Version,
            ["org.opencontainers.image.revision"] = Commit
        };

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
