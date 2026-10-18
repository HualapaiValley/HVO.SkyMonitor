using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment.ReleaseTool;

namespace HVO.SkyMonitor.Deployment.Distribution.Tests;

/// <summary>
/// <c>create-release</c> writes a release's files in the contract's shape (install contract section 4, RoofController's
/// release.json schema 1): installers, an offline archive per platform split from the image archive that is pushed,
/// release.json and SHA256SUMS. It refuses an image that is not the release's, and an upgrade note the installer would
/// refuse to print, so a release that could not be installed is never written.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ReleaseAssetsToolTests
{
    private static readonly string[] OfflineArchiveEntries =
        ["oci-layout", "index.json", "manifest.json", "blobs/", "blobs/sha256/"];

    private static readonly string[] ReleaseFiles =
    [
        "SHA256SUMS", "hvo-sky-install-linux-arm64", "hvo-sky-install-linux-x64", "release.json",
        "skymonitor-cameraagent-1.2.0-dev.3-linux-amd64.oci.tar", "skymonitor-cameraagent-1.2.0-dev.3-linux-arm64.oci.tar"
    ];

    private const string StateContractsLabel = "io.hvo.skymonitor.state-contracts";

    private static readonly string[] Platforms = ["linux/amd64", "linux/arm64"];

    [TestMethod]
    public async Task CreateRelease_WritesTheContractsAssetsReleaseJsonAndSha256Sums()
    {
        using var fixture = OciReleaseFixture.Create();
        fixture.WriteNote("1.1.0", "Eleven.\r\nSecond line.");
        fixture.WriteNote("1.2.0", "Twelve.\n\tIndented.");
        fixture.WriteNote("1.3.0", "A later release's notes are not this release's.");
        var output = Path.Combine(fixture.Root, "release");

        Assert.AreEqual(0, await Program.Main(fixture.Arguments(output)));

        var names = Directory.GetFiles(output).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(ReleaseFiles, names);
        var release = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(output, "release.json")))!.AsObject();
        Assert.AreEqual(1, (int)release["schemaVersion"]!);
        Assert.AreEqual("HVO SkyMonitor", (string)release["product"]!);
        Assert.AreEqual(OciReleaseFixture.Version, (string)release["version"]!);
        Assert.AreEqual($"v{OciReleaseFixture.Version}", (string)release["tag"]!);
        Assert.IsTrue((bool)release["prerelease"]!);
        Assert.AreEqual(OciReleaseFixture.Commit, (string)release["commit"]!);
        Assert.AreEqual(OciReleaseFixture.Created, (string)release["created"]!);
        Assert.AreEqual("https://github.com/HualapaiValley/HVO.SkyMonitor", (string)release["repository"]!);
        var image = release["images"]!["cameraAgent"]!.AsObject();
        Assert.AreEqual("ghcr.io/hualapaivalley/skymonitor-cameraagent", (string)image["repository"]!);
        Assert.AreEqual(OciReleaseFixture.Version, (string)image["tag"]!);
        Assert.AreEqual(fixture.IndexDigest, (string)image["digest"]!);
        Assert.AreEqual(
            $"ghcr.io/hualapaivalley/skymonitor-cameraagent:{OciReleaseFixture.Version}@{fixture.IndexDigest}", (string)image["reference"]!);
        CollectionAssert.AreEqual(Platforms, image["platforms"]!.AsArray().Select(static node => (string)node!).ToArray());
        // The image's state contract label, as the image declares it, under the image's key (install contract section 6).
        Assert.AreEqual($$"""{"cameraAgent":{{OciReleaseFixture.StateContracts}}}""", release["stateContracts"]!.ToJsonString());
        foreach (var archive in image["offlineArchives"]!.AsArray().Select(static node => node!.AsObject()))
        {
            var platform = (string)archive["platform"]!;
            var path = Path.Combine(output, (string)archive["name"]!);
            Assert.AreEqual(new FileInfo(path).Length, (long)archive["size"]!);
            Assert.AreEqual(Sha256(path), (string)archive["sha256"]!);
            Assert.AreEqual(fixture.Platforms[platform].Manifest, (string)archive["manifestDigest"]!);
            Assert.AreEqual(fixture.Platforms[platform].Config, (string)archive["configDigest"]!);
        }
        // Every note up to the release this pre-release belongs to, oldest first, with CRLF read as LF.
        Assert.AreEqual(
            """[{"version":"1.1.0","text":"Eleven.\nSecond line."},{"version":"1.2.0","text":"Twelve.\n\tIndented."}]""",
            release["upgradeNotes"]!.ToJsonString());
        var assets = release["assets"]!.AsArray().Select(static node => node!.AsObject()).ToArray();
        CollectionAssert.AreEqual(names.Where(static name => name is not "release.json" and not "SHA256SUMS").ToArray(),
            assets.Select(static asset => (string)asset["name"]!).ToArray());
        foreach (var asset in assets)
        {
            var path = Path.Combine(output, (string)asset["name"]!);
            Assert.AreEqual(new FileInfo(path).Length, (long)asset["size"]!);
            Assert.AreEqual(Sha256(path), (string)asset["sha256"]!);
        }
        Assert.AreEqual("installer linux-arm64", Describe(assets[0]));
        Assert.AreEqual("installer linux-x64", Describe(assets[1]));
        Assert.AreEqual("image-archive linux-x64", Describe(assets[2]));
        Assert.AreEqual("image-archive linux-arm64", Describe(assets[3]));
        // SHA256SUMS lists every file, release.json included, as sha256sum writes it, and not itself.
        var expected = string.Concat(names.Where(static name => name != "SHA256SUMS")
            .Select(name => $"{Sha256(Path.Combine(output, name!))}  {name}\n"));
        Assert.AreEqual(expected, await File.ReadAllTextAsync(Path.Combine(output, "SHA256SUMS")));
    }

    [TestMethod]
    public async Task CreateRelease_IdenticalInputs_ProduceIdenticalFiles()
    {
        using var fixture = OciReleaseFixture.Create();
        fixture.WriteNote("1.2.0", "Twelve.");
        var first = Path.Combine(fixture.Root, "first");
        var second = Path.Combine(fixture.Root, "second");

        Assert.AreEqual(0, await Program.Main(fixture.Arguments(first)));
        Assert.AreEqual(0, await Program.Main(fixture.Arguments(second)));

        foreach (var path in Directory.GetFiles(first))
        {
            CollectionAssert.AreEqual(
                await File.ReadAllBytesAsync(path), await File.ReadAllBytesAsync(Path.Combine(second, Path.GetFileName(path))),
                Path.GetFileName(path));
        }
    }

    /// <summary>
    /// The release.json the installer's own reader is tested against is this tool's output for the fixture's inputs, so
    /// what the release tool writes and what the installer reads cannot drift apart.
    /// </summary>
    [TestMethod]
    public async Task CreateRelease_WritesTheReleaseJsonTheInstallersReaderIsTestedWith()
    {
        using var fixture = OciReleaseFixture.Create();
        fixture.WriteNote("1.1.0", "Eleven.\r\nSecond line.");
        fixture.WriteNote("1.2.0", "Twelve.\n\tIndented.");
        var output = Path.Combine(fixture.Root, "release");

        Assert.AreEqual(0, await Program.Main(fixture.Arguments(output)));

        var golden = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ReleaseTool", "release.json");
        Assert.AreEqual(
            await File.ReadAllTextAsync(golden),
            await File.ReadAllTextAsync(Path.Combine(output, "release.json")),
            "Regenerate Fixtures/ReleaseTool/release.json from this test's output when the writer changes on purpose.");
    }

    [TestMethod]
    public async Task CreateRelease_OfflineArchive_HoldsTheReleaseIndexAndOnlyItsPlatformsBlobs()
    {
        using var fixture = OciReleaseFixture.Create();
        var output = Path.Combine(fixture.Root, "release");

        Assert.AreEqual(0, await Program.Main(fixture.Arguments(output)));

        var (manifest, config, layer) = fixture.Platforms["linux/arm64"];
        var entries = await ReadTarAsync(Path.Combine(output, "skymonitor-cameraagent-1.2.0-dev.3-linux-arm64.oci.tar"));
        CollectionAssert.AreEqual(
            OfflineArchiveEntries.Concat(new[] { fixture.IndexDigest, manifest, config, layer }
                .Select(static digest => $"blobs/sha256/{digest[7..]}")).ToArray(),
            entries.Keys.ToArray());
        foreach (var (name, bytes) in entries.Where(static entry => entry.Key.StartsWith("blobs/sha256/", StringComparison.Ordinal) &&
                                                                     !entry.Key.EndsWith('/')))
        {
            Assert.AreEqual(name["blobs/sha256/".Length..], Convert.ToHexStringLower(SHA256.HashData(bytes)), name);
        }
        var layout = JsonNode.Parse(entries["index.json"])!;
        Assert.AreEqual(fixture.IndexDigest, (string)layout["manifests"]![0]!["digest"]!);
        Assert.AreEqual(
            "ghcr.io/hualapaivalley/skymonitor-cameraagent:1.2.0-dev.3",
            (string)layout["manifests"]![0]!["annotations"]!["io.containerd.image.name"]!);
        var docker = JsonNode.Parse(entries["manifest.json"])![0]!;
        Assert.AreEqual($"blobs/sha256/{config[7..]}", (string)docker["Config"]!);
        Assert.AreEqual($"blobs/sha256/{layer[7..]}", (string)docker["Layers"]![0]!);
        Assert.AreEqual("ghcr.io/hualapaivalley/skymonitor-cameraagent:1.2.0-dev.3", (string)docker["RepoTags"]![0]!);
    }

    /// <summary>
    /// buildx writes a one-platform build without provenance as that platform's manifest, with no index over it. A release
    /// for that one platform names the manifest itself, which is also what the registry serves, and its offline archive
    /// holds no index.
    /// </summary>
    [TestMethod]
    public async Task CreateRelease_OnePlatformImageWithoutAnIndex_NamesItsManifest()
    {
        using var fixture = OciReleaseFixture.Create();
        fixture.Build(platforms: ["linux/amd64"], singleManifest: true);
        var output = Path.Combine(fixture.Root, "release");

        Assert.AreEqual(0, await Program.Main(fixture.Arguments(output, "--platforms", "linux/amd64")));

        var (manifest, config, layer) = fixture.Platforms["linux/amd64"];
        var image = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "release.json")))!["images"]!["cameraAgent"]!;
        Assert.AreEqual(manifest, (string)image["digest"]!);
        Assert.AreEqual($"ghcr.io/hualapaivalley/skymonitor-cameraagent:1.2.0-dev.3@{manifest}", (string)image["reference"]!);
        var archive = image["offlineArchives"]!.AsArray().Single()!;
        Assert.AreEqual("linux/amd64", (string)archive["platform"]!);
        Assert.AreEqual(manifest, (string)archive["manifestDigest"]!);
        Assert.AreEqual(config, (string)archive["configDigest"]!);
        var entries = await ReadTarAsync(Path.Combine(output, "skymonitor-cameraagent-1.2.0-dev.3-linux-amd64.oci.tar"));
        CollectionAssert.AreEqual(
            OfflineArchiveEntries.Concat(new[] { manifest, config, layer }.Select(static digest => $"blobs/sha256/{digest[7..]}")).ToArray(),
            entries.Keys.ToArray());
        var top = JsonNode.Parse(entries["index.json"])!["manifests"]!.AsArray().Single()!;
        Assert.AreEqual(OciReleaseFixture.ManifestMediaType, (string)top["mediaType"]!);
        Assert.AreEqual(manifest, (string)top["digest"]!);
    }

    [TestMethod]
    [DataRow("version", "The linux/arm64 image is not labelled as CameraAgent 1.2.0-dev.3 from commit 0123456789abcdef0123456789abcdef01234567.")]
    [DataRow("revision", "The linux/arm64 image is not labelled as CameraAgent 1.2.0-dev.3 from commit 0123456789abcdef0123456789abcdef01234567.")]
    [DataRow("component", "The linux/arm64 image is not labelled as CameraAgent 1.2.0-dev.3 from commit 0123456789abcdef0123456789abcdef01234567.")]
    [DataRow("created-missing", "The linux/arm64 image's org.opencontainers.image.created label is not the release's created time, 2026-10-17T18:00:00Z.")]
    [DataRow("created-other", "The linux/arm64 image's org.opencontainers.image.created label is not the release's created time, 2026-10-17T18:00:00Z.")]
    [DataRow("state-contracts-missing", "The linux/arm64 image's io.hvo.skymonitor.state-contracts label is not a state contract: a JSON object of stores, each {\"writes\": W, \"reads\": {\"min\": A, \"max\": B}} with whole numbers 1 <= A <= W <= B.")]
    [DataRow("state-contracts-not-json", "The linux/arm64 image's io.hvo.skymonitor.state-contracts label is not a state contract: a JSON object of stores, each {\"writes\": W, \"reads\": {\"min\": A, \"max\": B}} with whole numbers 1 <= A <= W <= B.")]
    [DataRow("state-contracts-not-an-object", "The linux/arm64 image's io.hvo.skymonitor.state-contracts label is not a state contract: a JSON object of stores, each {\"writes\": W, \"reads\": {\"min\": A, \"max\": B}} with whole numbers 1 <= A <= W <= B.")]
    [DataRow("state-contracts-store-not-an-object", "The linux/arm64 image's io.hvo.skymonitor.state-contracts label is not a state contract: a JSON object of stores, each {\"writes\": W, \"reads\": {\"min\": A, \"max\": B}} with whole numbers 1 <= A <= W <= B.")]
    [DataRow("state-contracts-store-name", "The linux/arm64 image's io.hvo.skymonitor.state-contracts label is not a state contract: a JSON object of stores, each {\"writes\": W, \"reads\": {\"min\": A, \"max\": B}} with whole numbers 1 <= A <= W <= B.")]
    [DataRow("state-contracts-duplicate-store", "The linux/arm64 image's io.hvo.skymonitor.state-contracts label is not a state contract: a JSON object of stores, each {\"writes\": W, \"reads\": {\"min\": A, \"max\": B}} with whole numbers 1 <= A <= W <= B.")]
    [DataRow("state-contracts-extra-member", "The linux/arm64 image's io.hvo.skymonitor.state-contracts label is not a state contract: a JSON object of stores, each {\"writes\": W, \"reads\": {\"min\": A, \"max\": B}} with whole numbers 1 <= A <= W <= B.")]
    [DataRow("state-contracts-reads-missing", "The linux/arm64 image's io.hvo.skymonitor.state-contracts label is not a state contract: a JSON object of stores, each {\"writes\": W, \"reads\": {\"min\": A, \"max\": B}} with whole numbers 1 <= A <= W <= B.")]
    [DataRow("state-contracts-reads-extra-member", "The linux/arm64 image's io.hvo.skymonitor.state-contracts label is not a state contract: a JSON object of stores, each {\"writes\": W, \"reads\": {\"min\": A, \"max\": B}} with whole numbers 1 <= A <= W <= B.")]
    [DataRow("state-contracts-writes-zero", "The linux/arm64 image's io.hvo.skymonitor.state-contracts label is not a state contract: a JSON object of stores, each {\"writes\": W, \"reads\": {\"min\": A, \"max\": B}} with whole numbers 1 <= A <= W <= B.")]
    [DataRow("state-contracts-writes-fraction", "The linux/arm64 image's io.hvo.skymonitor.state-contracts label is not a state contract: a JSON object of stores, each {\"writes\": W, \"reads\": {\"min\": A, \"max\": B}} with whole numbers 1 <= A <= W <= B.")]
    [DataRow("state-contracts-writes-text", "The linux/arm64 image's io.hvo.skymonitor.state-contracts label is not a state contract: a JSON object of stores, each {\"writes\": W, \"reads\": {\"min\": A, \"max\": B}} with whole numbers 1 <= A <= W <= B.")]
    [DataRow("state-contracts-writes-above-max", "The linux/arm64 image's io.hvo.skymonitor.state-contracts label is not a state contract: a JSON object of stores, each {\"writes\": W, \"reads\": {\"min\": A, \"max\": B}} with whole numbers 1 <= A <= W <= B.")]
    [DataRow("state-contracts-min-above-writes", "The linux/arm64 image's io.hvo.skymonitor.state-contracts label is not a state contract: a JSON object of stores, each {\"writes\": W, \"reads\": {\"min\": A, \"max\": B}} with whole numbers 1 <= A <= W <= B.")]
    [DataRow("state-contracts-other-platform", "The linux/arm64 image's io.hvo.skymonitor.state-contracts label is not the linux/amd64 image's.")]
    [DataRow("platform-missing", "The image index must list one image for each of linux/amd64, linux/arm64 and nothing else.")]
    [DataRow("platform-extra", "The image index must list one image for each of linux/amd64 and nothing else.")]
    [DataRow("two-images", "The image archive must hold exactly one image.")]
    [DataRow("not-an-index", "The image archive's image is not an index of platforms.")]
    [DataRow("manifest-for-two-platforms", "The image archive's image is not an index of platforms.")]
    [DataRow("manifest-for-another-platform", "The linux/amd64 image's configuration is for another platform.")]
    [DataRow("tampered-config", "In the image archive, the linux/arm64 configuration is not the blob {config} its descriptor names.")]
    [DataRow("tampered-layer", "In the image archive, a linux/arm64 layer is not the blob {layer} its descriptor names.")]
    public async Task CreateRelease_ImageThatIsNotTheReleases_FailsAndLeavesNoRelease(string fault, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var fixture = OciReleaseFixture.Create();
        string[] extra = [];
        switch (fault)
        {
            case "version": fixture.Build(labels: static (platform, labels) => Relabel(platform, labels, "org.opencontainers.image.version", "1.2.0-dev.4")); break;
            case "revision": fixture.Build(labels: static (platform, labels) => Relabel(platform, labels, "org.opencontainers.image.revision", new string('f', 40))); break;
            case "component": fixture.Build(labels: static (platform, labels) => Relabel(platform, labels, "io.hvo.skymonitor.component", "LogicHost")); break;
            case "created-missing": fixture.Build(labels: static (platform, labels) => Unlabel(platform, labels, "org.opencontainers.image.created")); break;
            case "created-other": fixture.Build(labels: static (platform, labels) => Relabel(platform, labels, "org.opencontainers.image.created", "2026-10-17T18:00:00+00:00")); break;
            case "state-contracts-missing": fixture.Build(labels: static (platform, labels) => Unlabel(platform, labels, StateContractsLabel)); break;
            case "state-contracts-not-json": fixture.Build(labels: static (platform, labels) => Relabel(platform, labels, StateContractsLabel, "{\"identity\":")); break;
            case "state-contracts-not-an-object": fixture.Build(labels: static (platform, labels) => Relabel(platform, labels, StateContractsLabel, "[]")); break;
            case "state-contracts-store-not-an-object": fixture.Build(labels: static (platform, labels) => Relabel(platform, labels, StateContractsLabel, "{\"identity\":1}")); break;
            case "state-contracts-store-name": fixture.Build(labels: static (platform, labels) => Relabel(platform, labels, StateContractsLabel, "{\"raw-ingress\":{\"writes\":15,\"reads\":{\"min\":13,\"max\":15}}}")); break;
            case "state-contracts-duplicate-store": fixture.Build(labels: static (platform, labels) => Relabel(platform, labels, StateContractsLabel, "{\"identity\":{\"writes\":1,\"reads\":{\"min\":1,\"max\":1}},\"identity\":{\"writes\":2,\"reads\":{\"min\":2,\"max\":2}}}")); break;
            case "state-contracts-extra-member": fixture.Build(labels: static (platform, labels) => Relabel(platform, labels, StateContractsLabel, "{\"identity\":{\"writes\":1,\"reads\":{\"min\":1,\"max\":1},\"migrates\":true}}")); break;
            case "state-contracts-reads-missing": fixture.Build(labels: static (platform, labels) => Relabel(platform, labels, StateContractsLabel, "{\"identity\":{\"writes\":1}}")); break;
            case "state-contracts-reads-extra-member": fixture.Build(labels: static (platform, labels) => Relabel(platform, labels, StateContractsLabel, "{\"identity\":{\"writes\":1,\"reads\":{\"min\":1,\"max\":1,\"newest\":1}}}")); break;
            case "state-contracts-writes-zero": fixture.Build(labels: static (platform, labels) => Relabel(platform, labels, StateContractsLabel, "{\"identity\":{\"writes\":0,\"reads\":{\"min\":0,\"max\":1}}}")); break;
            case "state-contracts-writes-fraction": fixture.Build(labels: static (platform, labels) => Relabel(platform, labels, StateContractsLabel, "{\"identity\":{\"writes\":1.0,\"reads\":{\"min\":1,\"max\":1}}}")); break;
            case "state-contracts-writes-text": fixture.Build(labels: static (platform, labels) => Relabel(platform, labels, StateContractsLabel, "{\"identity\":{\"writes\":\"1\",\"reads\":{\"min\":1,\"max\":1}}}")); break;
            case "state-contracts-writes-above-max": fixture.Build(labels: static (platform, labels) => Relabel(platform, labels, StateContractsLabel, "{\"rawIngress\":{\"writes\":16,\"reads\":{\"min\":13,\"max\":15}}}")); break;
            case "state-contracts-min-above-writes": fixture.Build(labels: static (platform, labels) => Relabel(platform, labels, StateContractsLabel, "{\"rawIngress\":{\"writes\":15,\"reads\":{\"min\":16,\"max\":16}}}")); break;
            case "state-contracts-other-platform": fixture.Build(labels: static (platform, labels) => Relabel(platform, labels, StateContractsLabel, "{\"identity\":{\"writes\":1,\"reads\":{\"min\":1,\"max\":1}}}")); break;
            case "platform-missing": fixture.Build(platforms: ["linux/amd64"]); break;
            case "platform-extra": extra = ["--platforms", "linux/amd64"]; break;
            case "two-images": fixture.Build(layout: static top => top["manifests"]!.AsArray().Add(top["manifests"]![0]!.DeepClone())); break;
            case "not-an-index": fixture.Build(layout: static top => top["manifests"]![0]!["mediaType"] = OciReleaseFixture.ManifestMediaType); break;
            case "manifest-for-two-platforms": fixture.Build(platforms: ["linux/amd64"], singleManifest: true); break;
            case "manifest-for-another-platform":
                fixture.Build(platforms: ["linux/arm64"], singleManifest: true);
                extra = ["--platforms", "linux/amd64"];
                break;
            case "tampered-config":
            case "tampered-layer":
                fixture.Build();
                var target = fault == "tampered-config" ? fixture.Platforms["linux/arm64"].Config : fixture.Platforms["linux/arm64"].Layer;
                fixture.Build(tamper: (name, bytes) => name.EndsWith(target[7..], StringComparison.Ordinal) ? [.. bytes, 0x20] : bytes);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(fault), fault, null);
        }
        var output = Path.Combine(fixture.Root, "release");
        using var error = new StringWriter();
        var standardError = Console.Error;
        Console.SetError(error);
        try
        {
            Assert.AreEqual(1, await Program.Main(fixture.Arguments(output, extra)));
        }
        finally
        {
            Console.SetError(standardError);
        }

        var expected = message
            .Replace("{config}", fixture.Platforms.GetValueOrDefault("linux/arm64").Config, StringComparison.Ordinal)
            .Replace("{layer}", fixture.Platforms.GetValueOrDefault("linux/arm64").Layer, StringComparison.Ordinal);
        Assert.AreEqual($"release error: {expected}{Environment.NewLine}", error.ToString());
        Assert.IsFalse(Directory.Exists(output), "a refused release must leave nothing behind");
    }

    /// <summary>
    /// The owner retired the key-signed manifests and indexes on 2026-10-16 (#1310): a release's trust is SHA256SUMS and
    /// its build provenance attestations, so the tool that writes a release signs nothing and verifies no signature.
    /// </summary>
    [TestMethod]
    [DataRow("create-installer")]
    [DataRow("create-catalog")]
    [DataRow("create-image")]
    [DataRow("create-index")]
    [DataRow("sign-local")]
    [DataRow("digest")]
    [DataRow("write-azure-signature")]
    [DataRow("verify")]
    [DataRow("verify-signature")]
    [DataRow("verify-index")]
    [DataRow("verify-attestation")]
    [DataRow("list-assets")]
    [DataRow("describe-image-archive")]
    [DataRow("image-index-digest")]
    public async Task RetiredSigningAndTrainCommand_IsAnUnknownCommand(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var error = new StringWriter();
        var standardError = Console.Error;
        Console.SetError(error);
        try
        {
            Assert.AreEqual(1, await Program.Main([command]));
        }
        finally
        {
            Console.SetError(standardError);
        }

        Assert.AreEqual($"release error: Unknown release-tool command '{command}'.{Environment.NewLine}", error.ToString());
    }

    [TestMethod]
    [DataRow("--version", "1.2.0+build", "--version must be a release version such as 1.2.0 or 1.2.0-dev.3.")]
    [DataRow("--version", "v1.2.0", "--version must be a release version such as 1.2.0 or 1.2.0-dev.3.")]
    [DataRow("--commit", "0123456", "--commit must be a lowercase 40-character Git object ID.")]
    [DataRow("--created-utc", "2026-10-17T18:00:00+01:00", "--created-utc must be an ISO-8601 UTC timestamp.")]
    [DataRow("--image-repository", "hualapaivalley/skymonitor-cameraagent", "--image-repository must name a registry and end in /skymonitor-cameraagent, such as ghcr.io/hualapaivalley/skymonitor-cameraagent.")]
    [DataRow("--image-repository", "ghcr.io/hualapaivalley/roof-controller", "--image-repository must name a registry and end in /skymonitor-cameraagent, such as ghcr.io/hualapaivalley/skymonitor-cameraagent.")]
    [DataRow("--platforms", "linux/386", "--platforms must list linux/amd64 and linux/arm64, or one of them, once each.")]
    [DataRow("--platforms", "linux/arm64,linux/arm64", "--platforms must list linux/amd64 and linux/arm64, or one of them, once each.")]
    public async Task CreateRelease_InvalidOption_IsRefusedBeforeTheOutputExists(string option, string value, string message)
    {
        using var fixture = OciReleaseFixture.Create();
        var output = Path.Combine(fixture.Root, "release");
        var arguments = fixture.Arguments(output).ToList();
        var position = arguments.IndexOf(option);
        if (position < 0)
        {
            arguments.AddRange([option, value]);
        }
        else
        {
            arguments[position + 1] = value;
        }
        using var error = new StringWriter();
        var standardError = Console.Error;
        Console.SetError(error);
        try
        {
            Assert.AreEqual(1, await Program.Main([.. arguments]));
        }
        finally
        {
            Console.SetError(standardError);
        }

        Assert.AreEqual($"release error: {message}{Environment.NewLine}", error.ToString());
        Assert.IsFalse(Directory.Exists(output));
    }

    [TestMethod]
    public async Task CreateRelease_InstallScript_IsAttachedWithTheReleasesVersionWrittenIn()
    {
        using var fixture = OciReleaseFixture.Create();
        var script = Path.Combine(fixture.Root, "install.sh");
        const string Unstamped = "#!/usr/bin/env bash\nmain() {\n  local release_version=''  # Written by the release tool.\n  : \"${release_version}\"\n}\nmain \"$@\"\n";
        await File.WriteAllTextAsync(script, Unstamped);
        var output = Path.Combine(fixture.Root, "release");

        Assert.AreEqual(0, await Program.Main(fixture.Arguments(output, "--install-script", script)));

        var written = Path.Combine(output, "install.sh");
        Assert.AreEqual(
            Unstamped.Replace(
                "  local release_version=''  # Written by the release tool.",
                $"  local release_version='{OciReleaseFixture.Version}'",
                StringComparison.Ordinal),
            await File.ReadAllTextAsync(written));
        Assert.IsTrue(File.GetUnixFileMode(written).HasFlag(UnixFileMode.UserExecute | UnixFileMode.OtherExecute));
        var release = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(output, "release.json")))!;
        var asset = release["assets"]!.AsArray().Single(static node => (string)node!["name"]! == "install.sh")!;
        Assert.AreEqual("install-script", (string)asset["kind"]!);
        Assert.IsNull(asset["platform"]);
        Assert.AreEqual(Sha256(written), (string)asset["sha256"]!);
        Assert.AreEqual(new FileInfo(written).Length, (long)asset["size"]!);
        CollectionAssert.Contains(await File.ReadAllLinesAsync(Path.Combine(output, "SHA256SUMS")), $"{Sha256(written)}  install.sh");
    }

    [TestMethod]
    public async Task CreateRelease_TheRepositorysInstallScript_ChangesInItsVersionLineAlone()
    {
        var source = Path.Combine(RepositoryRoot(), "src", "HVO.SkyMonitor.Deployment.Cli", "install.sh");
        using var fixture = OciReleaseFixture.Create();
        var output = Path.Combine(fixture.Root, "release");

        Assert.AreEqual(0, await Program.Main(fixture.Arguments(output, "--install-script", source)));

        var before = await File.ReadAllLinesAsync(source);
        var after = await File.ReadAllLinesAsync(Path.Combine(output, "install.sh"));
        Assert.HasCount(before.Length, after);
        var changed = Enumerable.Range(0, before.Length).Where(index => before[index] != after[index]).ToArray();
        Assert.HasCount(1, changed);
        StringAssert.StartsWith(before[changed[0]], "  local release_version=''", StringComparison.Ordinal);
        Assert.AreEqual($"  local release_version='{OciReleaseFixture.Version}'", after[changed[0]]);
    }

    [TestMethod]
    [DataRow("#!/usr/bin/env bash\nmain() { :; }\nmain \"$@\"\n", 0)]
    [DataRow("main() {\n  local release_version=''\n  local release_version=''  # again\n}\n", 2)]
    [DataRow("main() {\n  local release_version='1.1.0'\n}\n", 0)]
    [DataRow("main() {\n local release_version=''\n}\n", 0)]
    public async Task CreateRelease_InstallScriptWithoutOneVersionLine_IsRefusedBeforeTheOutputExists(string text, int lines)
    {
        using var fixture = OciReleaseFixture.Create();
        var script = Path.Combine(fixture.Root, "install.sh");
        await File.WriteAllTextAsync(script, text);
        var output = Path.Combine(fixture.Root, "release");
        using var error = new StringWriter();
        var standardError = Console.Error;
        Console.SetError(error);
        try
        {
            Assert.AreEqual(1, await Program.Main(fixture.Arguments(output, "--install-script", script)));
        }
        finally
        {
            Console.SetError(standardError);
        }

        Assert.AreEqual(
            $"release error: --install-script must name its release on one line, \"  local release_version=''\", and has {lines}." +
            Environment.NewLine,
            error.ToString());
        Assert.IsFalse(Directory.Exists(output), "a refused release must leave nothing behind");
    }

    [TestMethod]
    public async Task CreateRelease_CatalogBundle_IsAttachedUnderItsOwnVersionWithItsFilesChecksums()
    {
        var bundle = Environment.GetEnvironmentVariable("HVO_PRODUCTION_CATALOG_BUNDLE");
        if (string.IsNullOrEmpty(bundle)) Assert.Inconclusive("Set HVO_PRODUCTION_CATALOG_BUNDLE to attach the approved catalog.");
        using var fixture = OciReleaseFixture.Create();
        var output = Path.Combine(fixture.Root, "release");

        Assert.AreEqual(0, await Program.Main(fixture.Arguments(output, "--catalog-bundle", bundle)));

        var release = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(output, "release.json")))!;
        var asset = release["assets"]!.AsArray().Single(static node => (string)node!["kind"]! == "catalog")!;
        var packageVersion = (string)JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(bundle, "manifest.json")))!["package"]!["version"]!;
        Assert.AreEqual($"{packageVersion}.bundle.tar.gz", (string)asset["name"]!);
        Assert.IsNull(asset["platform"]);
        foreach (var file in asset["files"]!.AsObject())
        {
            Assert.AreEqual(Sha256(Path.Combine(bundle, file.Key)), (string)file.Value!, file.Key);
        }
        var archive = await ReadTarAsync(Path.Combine(output, $"{packageVersion}.bundle.tar.gz"), gzip: true);
        CollectionAssert.AreEquivalent(
            asset["files"]!.AsObject().Select(file => $"{packageVersion}.bundle/{file.Key}").ToArray(), archive.Keys.ToArray());
    }

    [TestMethod]
    [DataRow(0xFEFF, DisplayName = "byte-order mark")]
    [DataRow(0x200B, DisplayName = "zero-width space")]
    [DataRow(0x00AD, DisplayName = "soft hyphen")]
    [DataRow(0x200D, DisplayName = "zero-width joiner")]
    [DataRow(0x202E, DisplayName = "right-to-left override")]
    [DataRow(0x2028, DisplayName = "line separator")]
    [DataRow(0x2029, DisplayName = "paragraph separator")]
    [DataRow(0xE000, DisplayName = "private use")]
    [DataRow(0xFDD0, DisplayName = "noncharacter")]
    [DataRow(0xFFFE, DisplayName = "noncharacter FFFE")]
    [DataRow(0x001B, DisplayName = "escape")]
    [DataRow(0x000D, DisplayName = "lone carriage return")]
    [DataRow(0x0085, DisplayName = "next line")]
    [DataRow(0x000B, DisplayName = "vertical tab")]
    public void ReadUpgradeNotes_ScalarTheInstallerRefuses_FailsTheReleaseNamingIt(int scalar)
    {
        using var fixture = OciReleaseFixture.Create();
        fixture.WriteNote("1.2.0", $"Upgrade{char.ConvertFromUtf32(scalar)} now.");

        var exception = Assert.ThrowsExactly<ReleaseToolException>(() => Program.ReadUpgradeNotes(fixture.Notes, "1.2.0"));

        Assert.AreEqual(
            $"The upgrade notes for 1.2.0 contain U+{scalar:X4}, which the installer refuses to print to a terminal " +
            "(control, format, separator, private-use or noncharacter): remove it.",
            exception.Message);
    }

    [TestMethod]
    public void ReadUpgradeNotes_ByteOrderMarkAtTheStart_FailsTheRelease()
    {
        // The installer reads release.json's text, so a byte-order mark a text editor left in the file is a U+FEFF in it.
        using var fixture = OciReleaseFixture.Create();
        File.WriteAllBytes(Path.Combine(fixture.Notes, "1.2.0.md"), [0xEF, 0xBB, 0xBF, .. "Twelve."u8]);

        var exception = Assert.ThrowsExactly<ReleaseToolException>(() => Program.ReadUpgradeNotes(fixture.Notes, "1.2.0"));

        StringAssert.StartsWith(exception.Message, "The upgrade notes for 1.2.0 contain U+FEFF", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ReadUpgradeNotes_SafeTextInAnyScript_IsKeptWithLineFeedsAndTabs()
    {
        using var fixture = OciReleaseFixture.Create();
        const string text = "Ünïcödé — 日本語 ✓ 🔭\r\n\tIndented & <quoted>.";
        fixture.WriteNote("1.2.0", text);

        var notes = Program.ReadUpgradeNotes(fixture.Notes, "1.2.0");

        Assert.AreEqual("Ünïcödé — 日本語 ✓ 🔭\n\tIndented & <quoted>.", notes.Single().Text);
    }

    [TestMethod]
    [DataRow("1.2.0", "1.0.0 1.1.0 1.2.0")]
    [DataRow("1.2.0-rc.1", "1.0.0 1.1.0 1.2.0")]
    [DataRow("1.1.5", "1.0.0 1.1.0")]
    [DataRow("0.9.0", "")]
    public void ReadUpgradeNotes_NotesUpToTheReleaseTheVersionBelongsTo_AreReadOldestFirst(string version, string expected)
    {
        using var fixture = OciReleaseFixture.Create();
        foreach (var note in new[] { "1.10.0", "1.2.0", "1.0.0", "1.1.0" })
        {
            fixture.WriteNote(note, $"Notes for {note}.");
        }

        var notes = Program.ReadUpgradeNotes(fixture.Notes, version);

        Assert.AreEqual(expected, string.Join(' ', notes.Select(static note => note.Version)));
    }

    [TestMethod]
    [DataRow("README.md", "file")]
    [DataRow("1.2.md", "file")]
    [DataRow("v1.2.0.md", "file")]
    [DataRow("1.2.0-rc.1.md", "file")]
    [DataRow("1.3.0.md", "directory")]
    [DataRow("1.3.0.md", "link")]
    public void ReadUpgradeNotes_FolderHoldingAnythingButXyzMdFiles_FailsTheRelease(string name, string kind)
    {
        using var fixture = OciReleaseFixture.Create();
        var path = Path.Combine(fixture.Notes, name);
        switch (kind)
        {
            case "directory": Directory.CreateDirectory(path); break;
            case "link": File.CreateSymbolicLink(path, fixture.WriteNote("1.0.0", "One.")); break;
            default: File.WriteAllText(path, "Notes."); break;
        }

        var exception = Assert.ThrowsExactly<ReleaseToolException>(() => Program.ReadUpgradeNotes(fixture.Notes, "1.2.0"));

        Assert.AreEqual($"{fixture.Notes} must hold X.Y.Z.md files alone; {name} is not one.", exception.Message);
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("blank")]
    [DataRow("not-utf8")]
    [DataRow("too-long")]
    public void ReadUpgradeNotes_NoteThatIsNotPrintableText_FailsTheRelease(string fault)
    {
        using var fixture = OciReleaseFixture.Create();
        var path = Path.Combine(fixture.Notes, "1.2.0.md");
        switch (fault)
        {
            case "empty": File.WriteAllBytes(path, []); break;
            case "blank": File.WriteAllText(path, " \n\t\n"); break;
            case "not-utf8": File.WriteAllBytes(path, [0x54, 0xC3, 0x28]); break;
            default: File.WriteAllText(path, new string('a', Program.MaximumUpgradeNoteLength + 1)); break;
        }

        var exception = Assert.ThrowsExactly<ReleaseToolException>(() => Program.ReadUpgradeNotes(fixture.Notes, "1.2.0"));

        Assert.AreEqual(
            fault switch
            {
                "not-utf8" => "The upgrade notes for 1.2.0 are not UTF-8 text.",
                "too-long" => $"The upgrade notes for 1.2.0 are longer than {Program.MaximumUpgradeNoteLength} characters.",
                _ => "The upgrade notes for 1.2.0 are empty."
            },
            exception.Message);
    }

    [TestMethod]
    public void ReadUpgradeNotes_NotesLongerTogetherThanTheInstallerPrints_FailTheRelease()
    {
        using var fixture = OciReleaseFixture.Create();
        var each = Program.MaximumUpgradeNoteLength;
        for (var minor = 0; minor * each <= Program.MaximumUpgradeNotesLength; minor++)
        {
            fixture.WriteNote($"1.{minor}.0", new string('a', each));
        }

        var exception = Assert.ThrowsExactly<ReleaseToolException>(() => Program.ReadUpgradeNotes(fixture.Notes, "1.20.0"));

        StringAssert.Contains(exception.Message, $"keep them under {Program.MaximumUpgradeNotesLength}", StringComparison.Ordinal);
    }

    [TestMethod]
    public void UpgradeNoteRule_IsTheInstallersOwnSourceNotACopy()
    {
        // F8 of #1325: the release applies exactly the installer's rule, so the tool compiles the installer's file.
        var project = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "tools", "HVO.SkyMonitor.Deployment.ReleaseTool", "HVO.SkyMonitor.Deployment.ReleaseTool.csproj"));

        StringAssert.Contains(
            project, """<Compile Include="..\..\src\HVO.SkyMonitor.Deployment.Cli\UpgradeNoteText.cs" """, StringComparison.Ordinal);
        Assert.IsEmpty(Directory.GetFiles(Path.Combine(RepositoryRoot(), "tools"), "UpgradeNoteText.cs", SearchOption.AllDirectories));
    }

    private static void Relabel(string platform, JsonObject labels, string name, string value)
    {
        if (platform == "linux/arm64")
        {
            labels[name] = value;
        }
    }

    private static void Unlabel(string platform, JsonObject labels, string name)
    {
        if (platform == "linux/arm64")
        {
            labels.Remove(name);
        }
    }

    private static string Describe(JsonObject asset) => $"{(string)asset["kind"]!} {(string?)asset["platform"]}";

    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static async Task<Dictionary<string, byte[]>> ReadTarAsync(string path, bool gzip = false)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using var file = File.OpenRead(path);
        await using var stream = gzip ? new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress) : (Stream)file;
        using var reader = new TarReader(stream);
        while (await reader.GetNextEntryAsync(copyData: true) is { } entry)
        {
            using var buffer = new MemoryStream();
            if (entry.DataStream is not null)
            {
                await entry.DataStream.CopyToAsync(buffer);
            }
            entries.Add(entry.Name, buffer.ToArray());
        }
        return entries;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
