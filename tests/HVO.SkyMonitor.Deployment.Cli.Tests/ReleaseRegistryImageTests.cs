using System.Text;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The registry read that happens before a release image is pulled: everything is checked against the digest that names
/// it and against the release, and anything else is refused before Docker is asked to pull.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ReleaseRegistryImageTests
{
    private const string Platform = "linux/amd64";

    [TestMethod]
    public async Task ReadAsync_TheReleasesImage_ReturnsItsConfigurationAndEveryIdentityTheRegistryGave()
    {
        using var release = ReleaseFixture.Create();
        var registry = new FakeRegistry(release.RegistryContent);

        var (image, selection) = await ReleaseRegistryImage.ReadAsync(Selection(release), Docker(registry), CancellationToken.None);

        Assert.AreEqual("CameraAgent", image.Component);
        Assert.AreEqual(release.ConfigDigest(Platform), image.ImageId);
        Assert.AreEqual("registry", image.Source);
        CollectionAssert.AreEqual(
            new[] { release.Digest, release.ManifestDigest(Platform), release.ConfigDigest(Platform) }, selection.ImageIds!.ToArray());
        Assert.AreEqual(3, registry.Reads);
    }

    [TestMethod]
    public async Task ReadAsync_ReleaseWithoutAnArchiveForThePlatform_TakesTheIdentitiesFromTheRegistry()
    {
        using var release = ReleaseFixture.Create(archivePlatforms: ["linux/arm64"]);
        var registry = new FakeRegistry(release.RegistryContent);
        var manifest = (string)JsonNode.Parse(release.RegistryContent[release.Digest])!["manifests"]!.AsArray()
            .Single(entry => (string)entry!["platform"]!["architecture"]! == "amd64")!["digest"]!;

        var (_, selection) = await ReleaseRegistryImage.ReadAsync(Selection(release, archived: false), Docker(registry), CancellationToken.None);

        Assert.AreEqual(3, selection.ImageIds!.Count);
        Assert.AreEqual(release.Digest, selection.ImageIds[0]);
        Assert.AreEqual(manifest, selection.ImageIds[1]);
    }

    [TestMethod]
    public async Task ReadAsync_ARawManifestPrintedWithATrailingNewline_IsTheContentItsDigestNames()
    {
        using var release = ReleaseFixture.Create();
        var registry = new FakeRegistry(release.RegistryContent);

        var (image, _) = await ReleaseRegistryImage.ReadAsync(
            Selection(release),
            async (arguments, cancellationToken) =>
            {
                var result = await Docker(registry)(arguments, cancellationToken);
                return arguments.Contains("--raw") ? result with { StandardOutput = result.StandardOutput + "\n" } : result;
            },
            CancellationToken.None);

        Assert.AreEqual(release.ConfigDigest(Platform), image.ImageId);
    }

    [TestMethod]
    public async Task ReadAsync_ASinglePlatformRelease_ReadsItsManifestAsTheImage()
    {
        using var release = ReleaseFixture.Create(platforms: [Platform], singleManifest: true);
        var registry = new FakeRegistry(release.RegistryContent);

        var (image, selection) = await ReleaseRegistryImage.ReadAsync(Selection(release), Docker(registry), CancellationToken.None);

        Assert.AreEqual(release.ConfigDigest(Platform), image.ImageId);
        CollectionAssert.AreEqual(new[] { release.Digest, release.ConfigDigest(Platform) }, selection.ImageIds!.ToArray());
        Assert.AreEqual(2, registry.Reads, "a one-platform release has no index to read");
    }

    [TestMethod]
    [DataRow("other-content", "its image is not the content {digest} names")]
    [DataRow("other-manifest-content", "its {platform} manifest is not the content {manifest} names")]
    [DataRow("platform-missing", "its image index does not list {platform} once")]
    [DataRow("platform-twice", "its image index does not list {platform} once")]
    [DataRow("index-names-itself", "its {platform} manifest is not a platform manifest")]
    [DataRow("not-an-image", "it is neither an image index nor a platform manifest")]
    [DataRow("manifest-not-release-json", "its {platform} manifest is not the one release.json names")]
    [DataRow("config-not-release-json", "its {platform} configuration is not the one release.json names")]
    [DataRow("no-config", "its {platform} manifest names no configuration")]
    [DataRow("configuration-platform", "its configuration is not for {platform}")]
    public async Task ReadAsync_RegistryNotServingTheReleasesImage_IsRefusedNamingWhatDisagrees(string fault, string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        using var release = ReleaseFixture.Create();
        var content = new Dictionary<string, byte[]>(release.RegistryContent, StringComparer.Ordinal);
        var selection = Selection(release);
        var manifest = release.ManifestDigest(Platform);
        var config = release.ConfigDigest(Platform);
        // An index the registry serves under its own digest, as the test changes it, and named as the release's image.
        void ServeIndex(Func<JsonObject, JsonObject> change)
        {
            var index = change(JsonNode.Parse(content[release.Digest])!.AsObject());
            var bytes = Encoding.UTF8.GetBytes(index.ToJsonString());
            content[ReleaseFixture.DigestOf(bytes)] = bytes;
            selection = selection with
            {
                DigestReference = $"{release.Repository}@{ReleaseFixture.DigestOf(bytes)}",
                ImageIds = [ReleaseFixture.DigestOf(bytes), manifest, config]
            };
        }
        JsonArray Entries(JsonObject index) => index["manifests"]!.AsArray();
        switch (fault)
        {
            case "other-content":
                content[release.Digest] = "{}"u8.ToArray();
                break;
            case "other-manifest-content":
                content[manifest] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(content[manifest]).Replace("layers", "Layers", StringComparison.Ordinal));
                break;
            case "platform-missing":
                ServeIndex(index => { Entries(index).RemoveAt(Entries(index).IndexOf(Entries(index).Single(IsHost))); return index; });
                break;
            case "platform-twice":
                ServeIndex(index => { Entries(index).Add(Entries(index).Single(IsHost)!.DeepClone()); return index; });
                break;
            case "index-names-itself":
                // An index cannot hash to a digest it holds, so the entry names an index the registry also serves.
                var inner = Encoding.UTF8.GetBytes(new JsonObject { ["schemaVersion"] = 2, ["mediaType"] = ReleaseFixture.IndexMediaType, ["manifests"] = new JsonArray() }.ToJsonString());
                content[ReleaseFixture.DigestOf(inner)] = inner;
                ServeIndex(index => { Entries(index).Single(IsHost)!["digest"] = ReleaseFixture.DigestOf(inner); return index; });
                selection = selection with { ImageIds = null };
                break;
            case "not-an-image":
                ServeIndex(index => { index["mediaType"] = "application/vnd.oci.image.config.v1+json"; return index; });
                break;
            case "manifest-not-release-json":
                selection = selection with { ImageIds = [release.Digest, release.ManifestDigest("linux/arm64"), config] };
                break;
            case "config-not-release-json":
                selection = selection with { ImageIds = [release.Digest, manifest, release.ConfigDigest("linux/arm64")] };
                break;
            case "no-config":
                {
                    var changed = JsonNode.Parse(content[manifest])!.AsObject();
                    changed.Remove("config");
                    var bytes = Encoding.UTF8.GetBytes(changed.ToJsonString());
                    content[ReleaseFixture.DigestOf(bytes)] = bytes;
                    manifest = ReleaseFixture.DigestOf(bytes);
                    ServeIndex(index => { Entries(index).Single(IsHost)!["digest"] = manifest; return index; });
                    break;
                }
            case "configuration-platform":
                content[config] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(content[config]).Replace("\"amd64\"", "\"arm64\"", StringComparison.Ordinal));
                break;
        }

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            ReleaseRegistryImage.ReadAsync(selection, Docker(new FakeRegistry(content)), CancellationToken.None));

        var expected = reason
            .Replace("{digest}", selection.DigestReference[(selection.DigestReference.IndexOf('@', StringComparison.Ordinal) + 1)..], StringComparison.Ordinal)
            .Replace("{manifest}", manifest, StringComparison.Ordinal)
            .Replace("{platform}", Platform, StringComparison.Ordinal);
        Assert.AreEqual(
            $"The CameraAgent image {selection.DigestReference} in its registry is not the {Platform} image release v{release.Version} names: {expected}. Nothing was pulled.",
            exception.Message);
    }

    [TestMethod]
    [DataRow("org.opencontainers.image.version", "9.9.9")]
    [DataRow("org.opencontainers.image.revision", "ffffffffffffffffffffffffffffffffffffffff")]
    [DataRow("io.hvo.skymonitor.component", "LogicHost")]
    public async Task ReadAsync_ImageNotLabelledAsTheRelease_IsRefusedBeforeAnythingIsPulled(string label, string value)
    {
        var labels = ReleaseFixture.ReleaseLabels("1.2.0-dev.3");
        labels[label] = value;
        using var release = ReleaseFixture.Create(labels: labels);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            ReleaseRegistryImage.ReadAsync(Selection(release), Docker(new FakeRegistry(release.RegistryContent)), CancellationToken.None));

        Assert.AreEqual(
            $"The CameraAgent image {release.DigestReference} in its registry is not labelled as CameraAgent {release.Version} from " +
            $"commit {ReleaseFixture.Commit}, as release v{release.Version} requires. Nothing was pulled.",
            exception.Message);
    }

    [TestMethod]
    public async Task ReadAsync_RegistryThatCannotBeRead_IsRefusedNamingTheArchiveSource()
    {
        using var release = ReleaseFixture.Create();
        var registry = new FakeRegistry(release.RegistryContent) { Unavailable = "ERROR: failed to authorize: https://user:secret@ghcr.io/token denied" };

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            ReleaseRegistryImage.ReadAsync(Selection(release), Docker(registry), CancellationToken.None));

        StringAssert.StartsWith(
            exception.Message,
            $"The installer cannot read {release.DigestReference} from its registry before pulling it, so it pulls nothing (",
            StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, "the default image source (leave out --image-source registry)", StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.Message, "a credential in Docker's diagnostic reached the message");
        Assert.AreEqual(1, registry.Reads, "nothing further is read once the registry cannot be");
    }

    [TestMethod]
    public async Task ReadAsync_OutputLargerThanAnyManifest_IsRefused()
    {
        using var release = ReleaseFixture.Create();

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => ReleaseRegistryImage.ReadAsync(
            Selection(release),
            (_, _) => Task.FromResult(new ProcessResult(0, new string(' ', (4 * 1024 * 1024) + 1), string.Empty)),
            CancellationToken.None));

        StringAssert.EndsWith(exception.Message, "its metadata is larger than the installer reads. Nothing was pulled.", StringComparison.Ordinal);
    }

    private static bool IsHost(JsonNode? entry) => (string)entry!["platform"]!["architecture"]! == "amd64";

    /// <summary>
    /// The selection the acquirer makes for <see cref="Platform"/>: with the identities release.json gives when it lists an
    /// offline archive for the platform (<paramref name="archived"/>), and without them otherwise.
    /// </summary>
    private static ReleaseImageSelection Selection(ReleaseFixture release, bool archived = true)
        => new(
            $"v{release.Version}", release.Version, ReleaseFixture.Commit, Platform, release.DigestReference,
            archived ? [release.Digest, release.ManifestDigest(Platform), release.ConfigDigest(Platform)] : null);

    private static Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> Docker(FakeRegistry registry)
        => (arguments, _) => Task.FromResult(registry.Answer(arguments));
}
