using System.Text;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The release.json reader an install and an upgrade use before they change anything: a complete schema-1 document in
/// the contract's shape reads whole, and every member that is missing, malformed or inconsistent with the rest is
/// refused with the reason, so a damaged or forged release.json selects nothing.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ReleaseManifestTests
{
    private const string ArchiveName = "skymonitor-cameraagent-1.2.0-linux-arm64.oci.tar";
    private const string ImageName = ReleaseManifest.CameraAgentImageName;

    [TestMethod]
    public void Parse_CompleteSchema1Release_ReadsEveryMemberTheInstallerUses()
    {
        var manifest = ReleaseManifest.Parse(Fixture());

        Assert.AreEqual("1.2.0", manifest.Version);
        Assert.AreEqual("v1.2.0", manifest.Tag);
        Assert.IsFalse(manifest.Prerelease);
        Assert.AreEqual("0123456789abcdef0123456789abcdef01234567", manifest.Commit);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 2, 18, 0, 0, TimeSpan.Zero), manifest.Created);
        var image = manifest.CameraAgent!;
        Assert.AreEqual("ghcr.io/hualapaivalley/skymonitor-cameraagent", image.Repository);
        Assert.AreEqual("1.2.0", image.Tag);
        Assert.AreEqual($"sha256:{new string('a', 64)}", image.Digest);
        Assert.AreEqual($"ghcr.io/hualapaivalley/skymonitor-cameraagent@sha256:{new string('a', 64)}", image.DigestReference);
        Assert.AreEqual($"ghcr.io/hualapaivalley/skymonitor-cameraagent:1.2.0@sha256:{new string('a', 64)}", image.Reference);
        CollectionAssert.AreEqual(ReleaseManifest.ImagePlatforms.ToArray(), image.Platforms.ToArray());
        var archive = image.ArchiveFor("linux/arm64")!;
        Assert.AreEqual(new OfflineArchive(
            ArchiveName, "linux/arm64", 734003200, new string('b', 64),
            $"sha256:{new string('e', 64)}", $"sha256:{new string('f', 64)}"), archive);
        Assert.IsNull(image.ArchiveFor("linux/amd64"));
        Assert.AreEqual(
            "hvo-sky-install-linux-arm64 install.sh skymonitor-cameraagent-1.2.0-linux-arm64.oci.tar hyg-v4.2-p3-s2-r1.bundle.tar.gz",
            string.Join(' ', manifest.Assets.Select(static asset => asset.Name)));
        Assert.IsNull(manifest.Assets[1].Platform);
        Assert.AreEqual("1.0.0 1.1.0 1.2.0", UpgradeNotesTests.Versions(manifest.UpgradeNotes));
        Assert.AreEqual(
            "Read the Camera & rig page after upgrading.\n\nThe state contract is unchanged, so rollback is a swap.",
            manifest.UpgradeNotes[2].Text);
    }

    [TestMethod]
    [DataRow("absent")]
    [DataRow("null")]
    [DataRow("empty")]
    public void Parse_ReleaseWithoutUpgradeNotes_ReadsNone(string shape)
    {
        var manifest = ReleaseManifest.Parse(Document(root =>
        {
            switch (shape)
            {
                case "absent": root.Remove("upgradeNotes"); break;
                case "null": root["upgradeNotes"] = null; break;
                default: root["upgradeNotes"] = new JsonArray(); break;
            }
        }));

        Assert.AreEqual(0, manifest.UpgradeNotes.Count);
    }

    [TestMethod]
    [DataRow("1.3.0-dev.4")]
    [DataRow("1.3.0-rc.1")]
    public void Parse_PreRelease_IsAReleaseMarkedAsAPreRelease(string version)
    {
        var manifest = ReleaseManifest.Parse(Document(root => SetVersion(root, version)));

        Assert.AreEqual(version, manifest.Version);
        Assert.AreEqual($"v{version}", manifest.Tag);
        Assert.IsTrue(manifest.Prerelease);
    }

    [TestMethod]
    public void Parse_PreReleaseNotMarkedAsOne_IsRefused()
    {
        AssertRefused(
            Document(root =>
            {
                SetVersion(root, "1.3.0-dev.4");
                root["prerelease"] = false;
            }),
            "1.3.0-dev.4 is a pre-release, but it is not marked one");
    }

    [TestMethod]
    public void Parse_VersionWithBuildMetadata_IsRefused()
    {
        AssertRefused(
            Document(root => SetVersion(root, "1.3.0+0123456")),
            "its version 1.3.0+0123456 carries build metadata, which a release's version does not");
    }

    [TestMethod]
    public void Parse_ImageInALocalRegistry_IsRead()
    {
        // A test release pushed to a registry on this host, for an install by digest without GHCR.
        var manifest = ReleaseManifest.Parse(Document(root => SetRepository(root, $"localhost:5000/hualapaivalley/{ImageName}")));

        Assert.AreEqual($"localhost:5000/hualapaivalley/{ImageName}", manifest.CameraAgent!.Repository);
    }

    [TestMethod]
    [DataRow("absent")]
    [DataRow("null")]
    public void Parse_ImageWithoutOfflineArchives_HasNone(string shape)
    {
        var manifest = ReleaseManifest.Parse(Document(root =>
        {
            var image = Image(root);
            if (shape == "absent") image.Remove("offlineArchives");
            else image["offlineArchives"] = null;
            RemoveAsset(root, ArchiveName);
        }));

        Assert.AreEqual(0, manifest.CameraAgent!.OfflineArchives.Count);
    }

    [TestMethod]
    [DataRow("""[]""", "it is not a JSON object")]
    [DataRow("""{"version":"1.0.0"}""", "it has no schemaVersion")]
    [DataRow("""{"schemaVersion":"1","version":"1.0.0"}""", "it has no schemaVersion")]
    [DataRow("""{"schemaVersion":2,"version":"1.0.0"}""", "it is schema 2, and this installer reads schema 1")]
    [DataRow("""{"schemaVersion":0,"version":"1.0.0"}""", "it is schema 0, and this installer reads schema 1")]
    [DataRow("""{"schemaVersion":1.5,"version":"1.0.0"}""", "it is schema 1.5, and this installer reads schema 1")]
    [DataRow("""{"schemaVersion":1}""", "it names no release version")]
    [DataRow("""{"schemaVersion":1,"version":"v1.0.0"}""", "it names no release version")]
    [DataRow("""{"schemaVersion":1,"version":"1.0"}""", "it names no release version")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0\n"}""", "it names no release version")]
    [DataRow("""{"schemaVersion":1,"version":1}""", "it names no release version")]
    public void Parse_UnknownSchemaOrVersion_IsRefusedWithTheReason(string json, string reason)
        => AssertRefused(Encoding.UTF8.GetBytes(json), reason);

    [TestMethod]
    [DataRow("tag-missing", "its tag is not v1.2.0")]
    [DataRow("tag-other", "its tag is not v1.2.0")]
    [DataRow("prerelease-missing", "it does not say whether it is a pre-release")]
    [DataRow("prerelease-text", "it does not say whether it is a pre-release")]
    [DataRow("prerelease-true", "it is marked a pre-release, but 1.2.0 is a final release")]
    [DataRow("commit-short", "its commit is not a 40-character lowercase commit ID")]
    [DataRow("commit-upper", "its commit is not a 40-character lowercase commit ID")]
    [DataRow("created-missing", "its created time is not a UTC time such as 2026-10-17T18:00:00Z")]
    [DataRow("created-offset", "its created time is not a UTC time such as 2026-10-17T18:00:00Z")]
    [DataRow("created-fraction", "its created time is not a UTC time such as 2026-10-17T18:00:00Z")]
    [DataRow("repository-other", "it is not a release of https://github.com/HualapaiValley/HVO.SkyMonitor")]
    [DataRow("repository-missing", "it is not a release of https://github.com/HualapaiValley/HVO.SkyMonitor")]
    [DataRow("images-missing", "it lists no images")]
    [DataRow("images-list", "it lists no images")]
    [DataRow("image-missing", "it has no skymonitor-cameraagent image")]
    [DataRow("image-repository-hub", "the skymonitor-cameraagent image's repository is not a <registry>/<path>/skymonitor-cameraagent repository")]
    [DataRow("image-repository-name", "the skymonitor-cameraagent image's repository is not a <registry>/<path>/skymonitor-cameraagent repository")]
    [DataRow("image-repository-upper", "the skymonitor-cameraagent image's repository is not a <registry>/<path>/skymonitor-cameraagent repository")]
    [DataRow("image-repository-tagged", "the skymonitor-cameraagent image's repository is not a <registry>/<path>/skymonitor-cameraagent repository")]
    [DataRow("image-tag", "the skymonitor-cameraagent image's tag is not 1.2.0")]
    [DataRow("image-digest-upper", "the skymonitor-cameraagent image's digest is not sha256:<64 lowercase hex digits>")]
    [DataRow("image-digest-sha512", "the skymonitor-cameraagent image's digest is not sha256:<64 lowercase hex digits>")]
    [DataRow("image-reference-untagged", "the skymonitor-cameraagent image's reference is not its repository, its tag and its digest")]
    [DataRow("image-reference-other", "the skymonitor-cameraagent image's reference is not its repository, its tag and its digest")]
    [DataRow("platforms-missing", "the skymonitor-cameraagent image's platforms are not a list of linux/amd64 and linux/arm64")]
    [DataRow("platforms-empty", "the skymonitor-cameraagent image's platforms are not a list of linux/amd64 and linux/arm64")]
    [DataRow("platforms-unknown", "the skymonitor-cameraagent image's platforms are not a list of linux/amd64 and linux/arm64")]
    [DataRow("platforms-duplicate", "the skymonitor-cameraagent image's platforms are not a list of linux/amd64 and linux/arm64")]
    [DataRow("archives-object", "the skymonitor-cameraagent image's offline archives are not a list of at most one per platform")]
    [DataRow("archives-too-many", "the skymonitor-cameraagent image's offline archives are not a list of at most one per platform")]
    [DataRow("archive-platform-unlisted", "offline archive 1 is not for one of the image's platforms, once")]
    [DataRow("archive-platform-repeated", "offline archive 2 is not for one of the image's platforms, once")]
    [DataRow("archive-name", "offline archive 1 for linux/arm64 is not named skymonitor-cameraagent-1.2.0-linux-arm64.oci.tar")]
    [DataRow("archive-name-path", "offline archive 1 for linux/arm64 is not named skymonitor-cameraagent-1.2.0-linux-arm64.oci.tar")]
    [DataRow("archive-size-zero", "skymonitor-cameraagent-1.2.0-linux-arm64.oci.tar's size is not a number of bytes")]
    [DataRow("archive-size-text", "skymonitor-cameraagent-1.2.0-linux-arm64.oci.tar's size is not a number of bytes")]
    [DataRow("archive-sha256", "skymonitor-cameraagent-1.2.0-linux-arm64.oci.tar's sha256 is not 64 lowercase hex digits")]
    [DataRow("archive-manifest-digest", "skymonitor-cameraagent-1.2.0-linux-arm64.oci.tar's manifestDigest and configDigest are not each sha256:<64 lowercase hex digits>")]
    [DataRow("archive-config-digest", "skymonitor-cameraagent-1.2.0-linux-arm64.oci.tar's manifestDigest and configDigest are not each sha256:<64 lowercase hex digits>")]
    [DataRow("archive-not-an-asset", "the offline archive skymonitor-cameraagent-1.2.0-linux-arm64.oci.tar is not an image-archive asset of the same size and SHA-256")]
    [DataRow("archive-asset-kind", "the offline archive skymonitor-cameraagent-1.2.0-linux-arm64.oci.tar is not an image-archive asset of the same size and SHA-256")]
    [DataRow("archive-asset-platform", "the offline archive skymonitor-cameraagent-1.2.0-linux-arm64.oci.tar is not an image-archive asset of the same size and SHA-256")]
    [DataRow("archive-asset-size", "the offline archive skymonitor-cameraagent-1.2.0-linux-arm64.oci.tar is not an image-archive asset of the same size and SHA-256")]
    [DataRow("archive-asset-sha256", "the offline archive skymonitor-cameraagent-1.2.0-linux-arm64.oci.tar is not an image-archive asset of the same size and SHA-256")]
    [DataRow("assets-missing", "its assets are not a list")]
    [DataRow("assets-object", "its assets are not a list")]
    [DataRow("asset-not-object", "asset 1's name is not the file name of a release asset")]
    [DataRow("asset-name-path", "asset 1's name is not the file name of a release asset")]
    [DataRow("asset-name-hidden", "asset 1's name is not the file name of a release asset")]
    [DataRow("asset-name-release-json", "asset 1's name is not the file name of a release asset")]
    [DataRow("asset-name-checksums", "asset 1's name is not the file name of a release asset")]
    [DataRow("asset-duplicate", "it lists hvo-sky-install-linux-arm64 more than once")]
    [DataRow("asset-kind", "hvo-sky-install-linux-arm64's kind is not a lowercase word")]
    [DataRow("asset-platform-missing", "hvo-sky-install-linux-arm64's platform is not null or a runtime such as linux-arm64")]
    [DataRow("asset-platform-slash", "hvo-sky-install-linux-arm64's platform is not null or a runtime such as linux-arm64")]
    [DataRow("asset-size-negative", "hvo-sky-install-linux-arm64's size is not a number of bytes")]
    [DataRow("asset-size-zero", "hvo-sky-install-linux-arm64's size is not a number of bytes")]
    [DataRow("asset-sha256", "hvo-sky-install-linux-arm64's sha256 is not 64 lowercase hex digits")]
    [DataRow("asset-files", "hyg-v4.2-p3-s2-r1.bundle.tar.gz's files are not an object of file names and their SHA-256")]
    public void Parse_MalformedMember_IsRefusedWithTheReason(string fault, string reason)
        => AssertRefused(Document(root => Break(root, fault)), reason);

    [TestMethod]
    public void Parse_MoreAssetsThanTheLimit_IsRefused()
    {
        AssertRefused(
            Document(root =>
            {
                var assets = root["assets"]!.AsArray();
                for (var index = assets.Count; index <= ReleaseManifest.MaximumAssets; index++)
                {
                    assets.Add(new JsonObject
                    {
                        ["name"] = $"extra-{index}",
                        ["kind"] = "extra",
                        ["platform"] = null,
                        ["size"] = 1,
                        ["sha256"] = new string('1', 64)
                    });
                }
            }),
            $"it has more than {ReleaseManifest.MaximumAssets} assets");
    }

    [TestMethod]
    [DataRow("""{}""", "its upgrade notes are not a list")]
    [DataRow("""["text"]""", "upgrade note 1 is not a release's version and its text")]
    [DataRow("""[{"version":"1.0.0"}]""", "upgrade note 1 is not a release's version and its text")]
    [DataRow("""[{"version":"1.0.0","text":"  "}]""", "upgrade note 1 is not a release's version and its text")]
    [DataRow("""[{"version":"1.0","text":"x"}]""", "upgrade note 1 is not a release's version and its text")]
    [DataRow("""[{"version":"1.0.0\n","text":"x"}]""", "upgrade note 1 is not a release's version and its text")]
    [DataRow("""[{"version":"1.0.0","text":"x"},{"version":"1.1.0","text":"\u001b[2J"}]""", "upgrade note 2 (1.1.0) contains unsafe terminal character U+001B")]
    [DataRow("""[{"version":"1.0.0","text":"DO NOT UPGRADE\rSafe to proceed."}]""", "upgrade note 1 (1.0.0) contains unsafe terminal character U+000D")]
    [DataRow("""[{"version":"1.0.0","text":"One.\n\rTwo."}]""", "upgrade note 1 (1.0.0) contains unsafe terminal character U+000D")]
    [DataRow("""[{"version":"1.0.0","text":"One.\r"}]""", "upgrade note 1 (1.0.0) contains unsafe terminal character U+000D")]
    [DataRow("""[{"version":"1.1.0","text":"x"},{"version":"1.1.0+build","text":"y"}]""", "it has more than one upgrade note for 1.1.0+build")]
    public void Parse_MalformedUpgradeNotes_AreRefusedWithTheReason(string notes, string reason)
        => AssertRefused(Document(root => root["upgradeNotes"] = JsonNode.Parse(notes)), reason);

    [TestMethod]
    public void Parse_CrlfLineEndings_AreReadAsLineFeeds()
    {
        var manifest = ReleaseManifest.Parse(Document(root =>
            root["upgradeNotes"] = JsonNode.Parse("""[{"version":"1.0.0","text":"One.\r\n\r\nTwo.\r\n\tThree."}]""")));

        Assert.AreEqual("One.\n\nTwo.\n\tThree.", manifest.UpgradeNotes.Single().Text);
    }

    [TestMethod]
    public void Parse_InvalidJson_IsRefused()
    {
        var exception = Assert.ThrowsExactly<InstallerException>(
            () => ReleaseManifest.Parse(Encoding.UTF8.GetBytes("""{"schemaVersion":1,""")));

        Assert.AreEqual("The installer cannot read release.json: it is not valid JSON.", exception.Message);
    }

    [TestMethod]
    [DataRow("""{"schemaVersion":1,"version":"\uDC00"}""")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0","upgradeNotes":[{"version":"1.0.0","text":"Upgrade \uD800 now."}]}""")]
    [DataRow("""{"schemaVersion":1,"\uD800":true,"version":"1.0.0"}""")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0","images":[{"tag":"\uDBFFx"}]}""")]
    public void Parse_EscapedLoneSurrogate_IsRefusedWhereverItIs(string json)
    {
        var exception = Assert.ThrowsExactly<InstallerException>(() => ReleaseManifest.Parse(Encoding.UTF8.GetBytes(json)));

        Assert.AreEqual(
            "The installer will not use release.json: it contains a string that is not valid Unicode.", exception.Message);
    }

    [TestMethod]
    [DataRow("{\"schemaVersion\":1,\"version\":\"1.0.0\",\"upgradeNotes\":[{\"version\":\"1.0.0\",\"text\":\"Upgrade ", new byte[] { 0xC3, 0x28 }, " now.\"}]}")]
    [DataRow("{\"schemaVersion\":1,\"version\":\"1.0.0\",\"upgradeNotes\":[{\"version\":\"1.0.0\",\"text\":\"Upgrade ", new byte[] { 0xED, 0xA0, 0x80 }, " now.\"}]}")]
    [DataRow("{\"schemaVersion\":1,\"version\":\"1.0.0\",\"images\":{\"", new byte[] { 0xFF }, "\":[]}}")]
    public void Parse_InvalidUtf8_IsRefusedWhereverItIs(string before, byte[] invalid, string after)
    {
        byte[] json = [.. Encoding.UTF8.GetBytes(before), .. invalid, .. Encoding.UTF8.GetBytes(after)];

        AssertRefused(json, "it is not UTF-8 text");
    }

    [TestMethod]
    public void Parse_DocumentLargerThanTheBound_IsRefusedBeforeParsing()
        => AssertRefused(new byte[ReleaseManifest.MaximumLength + 1], $"it is larger than {ReleaseManifest.MaximumLength} bytes");

    [TestMethod]
    public void Parse_AsManyNotesAsTheLimit_AreReadAndOneMoreIsRefused()
    {
        Assert.HasCount(
            ReleaseManifest.MaximumUpgradeNotes, ReleaseManifest.Parse(Notes(ReleaseManifest.MaximumUpgradeNotes)).UpgradeNotes);

        AssertRefused(
            Notes(ReleaseManifest.MaximumUpgradeNotes + 1),
            $"it has more than {ReleaseManifest.MaximumUpgradeNotes} upgrade notes");
    }

    [TestMethod]
    public async Task Parse_TheSizeLimitsWorthOfDistinctNotes_IsRefusedForItsCountBeforeAnyIsCompared()
    {
        // 25,000 distinct notes fit in the size limit. Checked against every earlier note they took about 312 million
        // comparisons, minutes of work.
        var json = Notes(25_000);
        Assert.IsLessThan(ReleaseManifest.MaximumLength, json.Length);

        var exception = await Task.Run(() => Assert.ThrowsExactly<InstallerException>(() => ReleaseManifest.Parse(json)))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(
            $"The installer will not use release.json: it has more than {ReleaseManifest.MaximumUpgradeNotes} upgrade notes.",
            exception.Message);
    }

    [TestMethod]
    public async Task Parse_TheLargestValidRelease_IsReadInLinearTime()
    {
        // As many notes as the limit, each for a long pre-release that is costly to match, filling the size limit. Each
        // version is matched once and a repeated release is found in a set, so this reads in milliseconds; checking each
        // note against every earlier one matched half a million pairs and took tens of seconds.
        var prerelease = string.Join('.', Enumerable.Repeat("a", 450));
        var json = Notes(Enumerable.Range(0, ReleaseManifest.MaximumUpgradeNotes).Select(index => $"1.0.0-{prerelease}.{index}"));
        Assert.IsLessThan(ReleaseManifest.MaximumLength, json.Length);

        var manifest = await Task.Run(() => ReleaseManifest.Parse(json)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.HasCount(ReleaseManifest.MaximumUpgradeNotes, manifest.UpgradeNotes);
    }

    internal static byte[] Fixture()
    {
        using var stream = typeof(ReleaseManifestTests).Assembly.GetManifestResourceStream(
            "HVO.SkyMonitor.Deployment.Cli.Tests.ReleaseManifestSchema1.json")!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    internal static ReleaseManifest Release(string version, params (string Version, string Text)[] notes)
        => new(version, [.. notes.Select(static note => new UpgradeNote(note.Version, note.Text))]);

    /// <summary>The complete fixture, changed by <paramref name="change"/>.</summary>
    private static byte[] Document(Action<JsonObject> change)
    {
        var root = JsonNode.Parse(Fixture())!.AsObject();
        change(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    /// <summary>The complete fixture with a one-word note for each of <paramref name="count"/> distinct releases.</summary>
    private static byte[] Notes(int count) => Notes(Enumerable.Range(0, count).Select(static index => $"1.{index}.0"));

    private static byte[] Notes(IEnumerable<string> versions)
    {
        var root = JsonNode.Parse(Fixture())!.AsObject();
        root.Remove("upgradeNotes");
        var json = root.ToJsonString();
        // Spliced as text, which keeps building a document of a megabyte cheap.
        return Encoding.UTF8.GetBytes(
            json[..^1] + ",\"upgradeNotes\":[" +
            string.Join(',', versions.Select(static version => "{\"version\":\"" + version + "\",\"text\":\"x\"}")) + "]}");
    }

    private static void AssertRefused(byte[] json, string reason)
    {
        var exception = Assert.ThrowsExactly<InstallerException>(() => ReleaseManifest.Parse(json));

        Assert.AreEqual($"The installer will not use release.json: {reason}.", exception.Message);
    }

    private static JsonObject Image(JsonObject root) => root["images"]![ImageName]!.AsObject();

    private static JsonObject Archive(JsonObject root) => Image(root)["offlineArchives"]![0]!.AsObject();

    private static JsonObject Asset(JsonObject root, string name)
        => root["assets"]!.AsArray().Select(static node => node!.AsObject()).Single(asset => (string)asset["name"]! == name);

    private static void RemoveAsset(JsonObject root, string name) => root["assets"]!.AsArray().Remove(Asset(root, name));

    /// <summary>Moves the whole document to <paramref name="version"/>, keeping every member that names it consistent.</summary>
    private static void SetVersion(JsonObject root, string version)
    {
        root["version"] = version;
        root["tag"] = $"v{version}";
        root["prerelease"] = version.Contains('-', StringComparison.Ordinal);
        var image = Image(root);
        image["tag"] = version;
        image["reference"] = $"{(string)image["repository"]!}:{version}@{(string)image["digest"]!}";
        var archive = Archive(root);
        var name = OfflineArchive.NameFor(version, "linux/arm64");
        Asset(root, (string)archive["name"]!)["name"] = name;
        archive["name"] = name;
    }

    private static void SetRepository(JsonObject root, string repository)
    {
        var image = Image(root);
        image["repository"] = repository;
        image["reference"] = $"{repository}:{(string)image["tag"]!}@{(string)image["digest"]!}";
    }

    private static void Break(JsonObject root, string fault)
    {
        var image = Image(root);
        var archive = Archive(root);
        var installer = Asset(root, "hvo-sky-install-linux-arm64");
        switch (fault)
        {
            case "tag-missing": root.Remove("tag"); break;
            case "tag-other": root["tag"] = "v1.2.1"; break;
            case "prerelease-missing": root.Remove("prerelease"); break;
            case "prerelease-text": root["prerelease"] = "false"; break;
            case "prerelease-true": root["prerelease"] = true; break;
            case "commit-short": root["commit"] = "0123456"; break;
            case "commit-upper": root["commit"] = "0123456789ABCDEF0123456789ABCDEF01234567"; break;
            case "created-missing": root.Remove("created"); break;
            case "created-offset": root["created"] = "2026-11-02T18:00:00+01:00"; break;
            case "created-fraction": root["created"] = "2026-11-02T18:00:00.5Z"; break;
            case "repository-other": root["repository"] = "https://github.com/HualapaiValley/HVO.RoofController"; break;
            case "repository-missing": root.Remove("repository"); break;
            case "images-missing": root.Remove("images"); break;
            case "images-list": root["images"] = new JsonArray(); break;
            case "image-missing": root["images"]!.AsObject().Remove(ImageName); break;
            case "image-repository-hub": SetRepository(root, $"hualapaivalley/{ImageName}"); break;
            case "image-repository-name": SetRepository(root, "ghcr.io/hualapaivalley/roof-controller"); break;
            case "image-repository-upper": SetRepository(root, $"ghcr.io/HualapaiValley/{ImageName}"); break;
            case "image-repository-tagged": SetRepository(root, $"ghcr.io/hualapaivalley/{ImageName}:latest"); break;
            case "image-tag": image["tag"] = "1.2.1"; break;
            case "image-digest-upper": image["digest"] = $"sha256:{new string('A', 64)}"; break;
            case "image-digest-sha512": image["digest"] = $"sha512:{new string('a', 128)}"; break;
            case "image-reference-untagged": image["reference"] = $"{(string)image["repository"]!}@{(string)image["digest"]!}"; break;
            case "image-reference-other": image["reference"] = $"ghcr.io/example/other:1.2.0@{(string)image["digest"]!}"; break;
            case "platforms-missing": image.Remove("platforms"); break;
            case "platforms-empty": image["platforms"] = new JsonArray(); break;
            case "platforms-unknown": image["platforms"] = new JsonArray("linux/amd64", "linux/386"); break;
            case "platforms-duplicate": image["platforms"] = new JsonArray("linux/arm64", "linux/arm64"); break;
            case "archives-object": image["offlineArchives"] = new JsonObject(); break;
            case "archives-too-many":
                image["offlineArchives"]!.AsArray().Add(archive.DeepClone());
                image["offlineArchives"]!.AsArray().Add(archive.DeepClone());
                break;
            case "archive-platform-unlisted": image["platforms"] = new JsonArray("linux/amd64"); break;
            case "archive-platform-repeated": image["offlineArchives"]!.AsArray().Add(archive.DeepClone()); break;
            case "archive-name": archive["name"] = "cameraagent.tar"; break;
            case "archive-name-path": archive["name"] = $"../{ArchiveName}"; break;
            case "archive-size-zero": archive["size"] = 0; break;
            case "archive-size-text": archive["size"] = "734003200"; break;
            case "archive-sha256": archive["sha256"] = new string('B', 64); break;
            case "archive-manifest-digest": archive.Remove("manifestDigest"); break;
            case "archive-config-digest": archive["configDigest"] = new string('f', 64); break;
            case "archive-not-an-asset": RemoveAsset(root, ArchiveName); break;
            case "archive-asset-kind": Asset(root, ArchiveName)["kind"] = "installer"; break;
            case "archive-asset-platform": Asset(root, ArchiveName)["platform"] = "linux-x64"; break;
            case "archive-asset-size": Asset(root, ArchiveName)["size"] = 1; break;
            case "archive-asset-sha256": Asset(root, ArchiveName)["sha256"] = new string('1', 64); break;
            case "assets-missing": root.Remove("assets"); break;
            case "assets-object": root["assets"] = new JsonObject(); break;
            case "asset-not-object": root["assets"]!.AsArray().Insert(0, "hvo-sky-install-linux-arm64"); break;
            case "asset-name-path": installer["name"] = "../hvo-sky-install-linux-arm64"; break;
            case "asset-name-hidden": installer["name"] = ".hvo-sky-install-linux-arm64"; break;
            case "asset-name-release-json": installer["name"] = ReleaseManifest.FileName; break;
            case "asset-name-checksums": installer["name"] = ReleaseChecksums.FileName; break;
            case "asset-duplicate": root["assets"]!.AsArray().Add(installer.DeepClone()); break;
            case "asset-kind": installer["kind"] = "Installer"; break;
            case "asset-platform-missing": installer.Remove("platform"); break;
            case "asset-platform-slash": installer["platform"] = "linux/arm64"; break;
            case "asset-size-negative": installer["size"] = -1; break;
            case "asset-size-zero": installer["size"] = 0; break;
            case "asset-sha256": installer["sha256"] = new string('C', 64); break;
            case "asset-files": Asset(root, "hyg-v4.2-p3-s2-r1.bundle.tar.gz")["files"] = new JsonObject { ["manifest.json"] = "x" }; break;
            default: throw new ArgumentOutOfRangeException(nameof(fault), fault, null);
        }
    }
}
