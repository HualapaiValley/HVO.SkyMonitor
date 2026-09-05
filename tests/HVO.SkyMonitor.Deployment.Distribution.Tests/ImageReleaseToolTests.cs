using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Deployment.Distribution;

namespace HVO.SkyMonitor.Deployment.Distribution.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ImageReleaseToolTests
{
    private const string Revision = "1f5c1a3b7d9e2f4a6b8c0d1e3f5a7b9c1d3e5f70";
    private const string Tree = "0a1b2c3d4e5f60718293a4b5c6d7e8f901234567";
    private const string MinimumRevision = "70ecdd3a0d02a5288aaa6438e3a5cfc8e395545f";
    private const string IndexDigest = "sha256:" + "3c" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd";
    private const string Repository = "ghcr.io/roysalisbury/hvo.skymonitor/cameraagent";
    private static readonly string[] ExpectedArchitectures = ["amd64", "arm64"];

    [TestMethod]
    public async Task CreateImage_SignedRelease_BindsEveryPlatformDigestAndCompatibilityToTheArchiveBytes()
    {
        using var fixture = ImageReleaseFixture.Create();
        var release = Path.Combine(fixture.Root, "release");

        Assert.AreEqual(0, await ReleaseTool.Program.Main(fixture.CreateArguments(release)));

        var manifestPath = Path.Combine(release, "image-manifest.json");
        var signaturePath = manifestPath + ".sig";
        Assert.AreEqual(0, await ReleaseTool.Program.Main(
            ["sign-local", "--manifest", manifestPath, "--private-key", fixture.PrivateKey, "--signature", signaturePath]));
        Assert.AreEqual(0, await ReleaseTool.Program.Main([
            "verify", "--manifest", manifestPath, "--signature", signaturePath, "--asset-root", release,
            "--public-key", fixture.PublicKey
        ]));

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        var signatureBytes = await File.ReadAllBytesAsync(signaturePath);
        var manifest = DistributionVerifier.VerifyManifest(manifestBytes, signatureBytes, fixture.TrustRoot);
        Assert.AreEqual(DistributionManifestKind.ImageRelease, manifest.ManifestKind);
        Assert.AreEqual("image", manifest.Release.Train);
        Assert.AreEqual("image-v1.2.3", manifest.Release.Tag);
        var image = manifest.Images.Single();
        Assert.AreEqual("CameraAgent", image.Component);
        Assert.AreEqual(Repository, image.Repository);
        Assert.AreEqual(IndexDigest, image.ManifestDigest);
        Assert.AreEqual("cameraagent-state-v2", image.Compatibility.StateContract);
        Assert.AreEqual(MinimumRevision, image.Compatibility.MinimumCompatibleRevision);
        Assert.AreEqual(12, image.Compatibility.RawIngressSchema);
        Assert.AreEqual(2, image.Compatibility.CatalogManifestVersion);
        Assert.AreEqual("local-replay-runner-v1", image.Compatibility.ReplayRunnerContract);
        CollectionAssert.AreEqual(
            ExpectedArchitectures,
            image.Platforms.Select(static platform => platform.Architecture).Order(StringComparer.Ordinal).ToArray());
        foreach (var platform in image.Platforms)
        {
            Assert.AreEqual("linux", platform.OperatingSystem);
            Assert.AreEqual(fixture.ManifestDigestFor(platform.Architecture), platform.ManifestDigest);
            Assert.AreEqual(fixture.ImageIdFor(platform.Architecture), platform.OfflineArchiveImageId);
            Assert.AreEqual($"cameraagent-image-v1.2.3-linux-{platform.Architecture}.tar", platform.OfflineArchiveAsset);
            Assert.IsTrue(File.Exists(Path.Combine(release, platform.OfflineArchiveAsset!)));
        }
        Assert.AreEqual(1, manifest.Artifacts.Count(static artifact => artifact.Role == DistributionArtifactRole.VulnerabilityScan));
        Assert.AreEqual(2, manifest.Artifacts.Count(static artifact => artifact.Role == DistributionArtifactRole.ImageArchive));
    }

    [TestMethod]
    public async Task CreateImage_ThenIndex_PublishesTheImageTrainWithAVersionedTag()
    {
        using var fixture = ImageReleaseFixture.Create();
        var release = Path.Combine(fixture.Root, "release");
        Assert.AreEqual(0, await ReleaseTool.Program.Main(fixture.CreateArguments(release)));
        var manifestPath = Path.Combine(release, "image-manifest.json");
        var manifestSignature = manifestPath + ".sig";
        Assert.AreEqual(0, await ReleaseTool.Program.Main(
            ["sign-local", "--manifest", manifestPath, "--private-key", fixture.PrivateKey, "--signature", manifestSignature]));
        var indexRoot = Path.Combine(fixture.Root, "index");

        Assert.AreEqual(0, await ReleaseTool.Program.Main([
            "create-index", "--train", "image", "--sequence", "1", "--created-utc", "2026-08-24T04:29:18Z",
            "--manifest", manifestPath, "--manifest-signature", manifestSignature, "--public-key", fixture.PublicKey,
            "--signing-key-id", fixture.KeyId, "--output", indexRoot
        ]));

        var indexPath = Path.Combine(indexRoot, "image-release-index.json");
        var indexSignature = indexPath + ".sig";
        Assert.AreEqual(0, await ReleaseTool.Program.Main([
            "sign-local", "--manifest", indexPath, "--private-key", fixture.PrivateKey, "--signature", indexSignature,
            "--metadata-kind", "index"
        ]));
        var indexBytes = await File.ReadAllBytesAsync(indexPath);
        var indexSignatureBytes = await File.ReadAllBytesAsync(indexSignature);
        var index = DistributionVerifier.VerifyIndex(indexBytes, indexSignatureBytes, fixture.TrustRoot);
        Assert.AreEqual("image", index.Train);
        Assert.AreEqual("image-v1.2.3", index.Releases.Single().Tag);
    }

    [TestMethod]
    public async Task CreateImage_ArchitectureThatContradictsTheArchive_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--linux-arm64") + 1] = fixture.Amd64Archive;

        Assert.AreEqual(1, await ReleaseTool.Program.Main(arguments));
    }

    [TestMethod]
    public async Task CreateImage_PlatformsThatDeclareDifferentCompatibility_AreRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var labels = ImageReleaseFixture.DefaultLabels(Revision);
        labels["io.hvo.skymonitor.catalog-contract"] = "hyg-v42-production-p3-s3";
        var divergent = fixture.WriteArchive("divergent-arm64", "arm64", labels);
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--linux-arm64") + 1] = divergent;

        Assert.AreEqual(1, await ReleaseTool.Program.Main(arguments));
    }

    [TestMethod]
    public async Task CreateImage_ImageMissingACompatibilityLabel_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var labels = ImageReleaseFixture.DefaultLabels(Revision);
        labels.Remove("io.hvo.skymonitor.raw-ingress-schema");
        var incomplete = fixture.WriteArchive("incomplete-amd64", "amd64", labels);
        var incompleteArm = fixture.WriteArchive("incomplete-arm64", "arm64", labels);
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--linux-amd64") + 1] = incomplete;
        arguments[Array.IndexOf(arguments, "--linux-arm64") + 1] = incompleteArm;

        Assert.AreEqual(1, await ReleaseTool.Program.Main(arguments));
    }

    [TestMethod]
    public async Task CreateImage_ImageBuiltFromAnotherRevision_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var labels = ImageReleaseFixture.DefaultLabels(new string('c', 40));
        var amd64 = fixture.WriteArchive("other-amd64", "amd64", labels);
        var arm64 = fixture.WriteArchive("other-arm64", "arm64", labels);
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--linux-amd64") + 1] = amd64;
        arguments[Array.IndexOf(arguments, "--linux-arm64") + 1] = arm64;

        Assert.AreEqual(1, await ReleaseTool.Program.Main(arguments));
    }

    [TestMethod]
    public async Task CreateImage_ScanReportWithACriticalFinding_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var report = fixture.WriteScanReport("critical-scan.json", critical: 1, fixture.AllImageIds);
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--scan-report") + 1] = report;

        Assert.AreEqual(1, await ReleaseTool.Program.Main(arguments));
    }

    [TestMethod]
    public async Task CreateImage_ScanReportThatOmitsAPublishedImage_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var report = fixture.WriteScanReport("partial-scan.json", critical: 0, [fixture.AllImageIds[0]]);
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--scan-report") + 1] = report;

        Assert.AreEqual(1, await ReleaseTool.Program.Main(arguments));
    }

    [TestMethod]
    public async Task Verify_SignedPlatformIdentityThatTheArchiveDoesNotProduce_Fails()
    {
        using var fixture = ImageReleaseFixture.Create();
        var release = Path.Combine(fixture.Root, "release");
        Assert.AreEqual(0, await ReleaseTool.Program.Main(fixture.CreateArguments(release)));
        var manifestPath = Path.Combine(release, "image-manifest.json");
        var text = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(
            manifestPath,
            text.Replace(fixture.ImageIdFor("arm64"), "sha256:" + new string('9', 64), StringComparison.Ordinal),
            new UTF8Encoding(false));
        var signaturePath = manifestPath + ".sig";
        Assert.AreEqual(0, await ReleaseTool.Program.Main(
            ["sign-local", "--manifest", manifestPath, "--private-key", fixture.PrivateKey, "--signature", signaturePath]));

        Assert.AreEqual(1, await ReleaseTool.Program.Main([
            "verify", "--manifest", manifestPath, "--signature", signaturePath, "--asset-root", release,
            "--public-key", fixture.PublicKey
        ]));
    }

    [TestMethod]
    public async Task Verify_ImageArchiveWhoseBytesChanged_Fails()
    {
        using var fixture = ImageReleaseFixture.Create();
        var release = Path.Combine(fixture.Root, "release");
        Assert.AreEqual(0, await ReleaseTool.Program.Main(fixture.CreateArguments(release)));
        var manifestPath = Path.Combine(release, "image-manifest.json");
        var signaturePath = manifestPath + ".sig";
        Assert.AreEqual(0, await ReleaseTool.Program.Main(
            ["sign-local", "--manifest", manifestPath, "--private-key", fixture.PrivateKey, "--signature", signaturePath]));
        var archive = Path.Combine(release, "cameraagent-image-v1.2.3-linux-arm64.tar");
        File.Copy(fixture.Amd64Archive, archive, overwrite: true);

        Assert.AreEqual(1, await ReleaseTool.Program.Main([
            "verify", "--manifest", manifestPath, "--signature", signaturePath, "--asset-root", release,
            "--public-key", fixture.PublicKey
        ]));
    }

    [TestMethod]
    public async Task Inspect_ArchiveWithARewrittenConfigurationBlob_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var tampered = fixture.WriteTamperedArchive("tampered-amd64");
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--linux-amd64") + 1] = tampered;

        Assert.AreEqual(1, await ReleaseTool.Program.Main(arguments));
    }

    private sealed class ImageReleaseFixture : IDisposable
    {
        private readonly Dictionary<string, string> manifestDigests = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> imageIds = new(StringComparer.Ordinal);

        private ImageReleaseFixture(string root, DistributionTrustRoot trustRoot)
        {
            Root = root;
            TrustRoot = trustRoot;
            PrivateKey = Path.Combine(root, "private.pem");
            PublicKey = Path.Combine(root, "public.pem");
        }

        public string Root { get; }
        public string PrivateKey { get; }
        public string PublicKey { get; }
        public string KeyId => TrustRoot.KeyId;
        public DistributionTrustRoot TrustRoot { get; }
        public string Amd64Archive { get; private set; } = string.Empty;
        public string Arm64Archive { get; private set; } = string.Empty;
        public string Dockerfile { get; private set; } = string.Empty;
        public string Notices { get; private set; } = string.Empty;
        public string ScanReport { get; private set; } = string.Empty;
        public string[] AllImageIds => [imageIds["amd64"], imageIds["arm64"]];

        public string ManifestDigestFor(string architecture) => manifestDigests[architecture];

        public string ImageIdFor(string architecture) => imageIds[architecture];

        public static ImageReleaseFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"hvo-image-release-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var fixture = new ImageReleaseFixture(root, DistributionTrustRoot.FromPem(key.ExportSubjectPublicKeyInfoPem()));
            File.WriteAllText(fixture.PrivateKey, key.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(fixture.PublicKey, key.ExportSubjectPublicKeyInfoPem());
            fixture.Dockerfile = Path.Combine(root, "Dockerfile");
            File.WriteAllText(fixture.Dockerfile, "FROM scratch\n");
            fixture.Notices = Path.Combine(root, "notices.md");
            File.WriteAllText(fixture.Notices, "test notices\n");
            fixture.Amd64Archive = fixture.WriteArchive("cameraagent-amd64", "amd64", DefaultLabels(Revision));
            fixture.Arm64Archive = fixture.WriteArchive("cameraagent-arm64", "arm64", DefaultLabels(Revision));
            fixture.ScanReport = fixture.WriteScanReport("scan.json", critical: 0, fixture.AllImageIds);
            return fixture;
        }

        public static Dictionary<string, string> DefaultLabels(string revision) => new(StringComparer.Ordinal)
        {
            ["io.hvo.skymonitor.component"] = "CameraAgent",
            ["io.hvo.skymonitor.state-compatibility"] = "cameraagent-state-v2",
            ["io.hvo.skymonitor.minimum-compatible-revision"] = MinimumRevision,
            ["io.hvo.skymonitor.identity-migration"] = "20260827053715_InitialIdentity",
            ["io.hvo.skymonitor.raw-ingress-schema"] = "12",
            ["io.hvo.skymonitor.catalog-manifest-version"] = "2",
            ["io.hvo.skymonitor.configuration-contract"] = "cameraagent-install-v1",
            ["io.hvo.skymonitor.catalog-contract"] = "hyg-v42-production-p3-s2",
            ["io.hvo.skymonitor.replay-runner-contract"] = "local-replay-runner-v1",
            ["org.opencontainers.image.revision"] = revision
        };

        public string[] CreateArguments(string output)
            =>
            [
                "create-image", "--version", "1.2.3", "--revision", Revision, "--tree", Tree,
                "--created-utc", "2026-08-24T04:29:18Z", "--repository", Repository, "--index-digest", IndexDigest,
                "--linux-amd64", Amd64Archive, "--linux-arm64", Arm64Archive, "--dockerfile", Dockerfile,
                "--scan-report", ScanReport, "--notices", Notices, "--signing-key-id", KeyId, "--output", output
            ];

        public string WriteScanReport(string name, int critical, IReadOnlyList<string> subjects)
        {
            var path = Path.Combine(Root, name);
            var report = new
            {
                schemaVersion = 1,
                scanner = "trivy",
                scannerVersion = "0.60.0",
                scannedUtc = "2026-08-24T04:29:18Z",
                subjects = subjects.Select(static imageId => new { imageId }).ToArray(),
                summary = new { critical, high = 0, medium = 0, low = 0, unknown = 0 },
                findings = Array.Empty<object>()
            };
            File.WriteAllText(path, JsonSerializer.Serialize(report), new UTF8Encoding(false));
            return path;
        }

        /// <summary>Writes a single-platform OCI archive shaped like a <c>buildx --output type=docker</c> result.</summary>
        public string WriteArchive(string name, string architecture, IReadOnlyDictionary<string, string> labels)
        {
            var configuration = JsonSerializer.SerializeToUtf8Bytes(new
            {
                architecture,
                os = "linux",
                config = new { Labels = labels },
                rootfs = new { type = "layers", diff_ids = Array.Empty<string>() }
            });
            var configDigest = Digest(configuration);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 2,
                mediaType = "application/vnd.oci.image.manifest.v1+json",
                config = new
                {
                    mediaType = "application/vnd.oci.image.config.v1+json",
                    digest = configDigest,
                    size = configuration.Length
                },
                layers = Array.Empty<object>()
            });
            var manifestDigest = Digest(manifest);
            var index = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 2,
                mediaType = "application/vnd.oci.image.index.v1+json",
                manifests = new[]
                {
                    new
                    {
                        mediaType = "application/vnd.oci.image.manifest.v1+json",
                        digest = manifestDigest,
                        size = manifest.Length
                    }
                }
            });
            var path = Path.Combine(Root, $"{name}.tar");
            WriteTar(path, new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["oci-layout"] = "{\"imageLayoutVersion\":\"1.0.0\"}"u8.ToArray(),
                ["index.json"] = index,
                [$"blobs/sha256/{manifestDigest["sha256:".Length..]}"] = manifest,
                [$"blobs/sha256/{configDigest["sha256:".Length..]}"] = configuration
            });
            manifestDigests[architecture] = manifestDigest;
            imageIds[architecture] = configDigest;
            return path;
        }

        /// <summary>Writes an archive whose configuration blob no longer hashes to the name it is stored under.</summary>
        public string WriteTamperedArchive(string name)
        {
            var source = Path.Combine(Root, $"{name}.tar");
            var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            using (var file = File.OpenRead(Amd64Archive))
            using (var reader = new TarReader(file))
            {
                TarEntry? entry;
                while ((entry = reader.GetNextEntry()) is not null)
                {
                    using var buffer = new MemoryStream();
                    entry.DataStream!.CopyTo(buffer);
                    entries[entry.Name] = buffer.ToArray();
                }
            }
            var configEntry = entries.Single(pair =>
                pair.Key.StartsWith("blobs/sha256/", StringComparison.Ordinal) &&
                pair.Key["blobs/sha256/".Length..] == ImageIdFor("amd64")["sha256:".Length..]);
            entries[configEntry.Key] = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(configEntry.Value).Replace("cameraagent-state-v2", "cameraagent-state-v9", StringComparison.Ordinal));
            WriteTar(source, entries);
            return source;
        }

        private static void WriteTar(string path, IReadOnlyDictionary<string, byte[]> entries)
        {
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var tar = new TarWriter(file, TarEntryFormat.Pax, leaveOpen: true);
            foreach (var (name, content) in entries.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            {
                using var stream = new MemoryStream(content);
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = stream,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                });
            }
        }

        private static string Digest(byte[] content) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
