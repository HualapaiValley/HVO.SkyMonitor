using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Deployment.Distribution;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class SignedImageReleaseTests
{
    private const string Revision = "1f5c1a3b7d9e2f4a6b8c0d1e3f5a7b9c1d3e5f70";
    private const string MinimumRevision = "70ecdd3a0d02a5288aaa6438e3a5cfc8e395545f";

    [TestMethod]
    public async Task AcquireImageAsync_NoSignedReleaseNamed_LeavesTheOperatorSuppliedImageAlone()
    {
        using var fixture = ImageDistributionFixture.Create();
        using var acquirer = new DistributionAcquirer(cacheRoot: fixture.CacheRoot, trustRoot: fixture.TrustRoot);

        var acquired = await acquirer.AcquireImageAsync(
            new InstallRequest
            {
                FriendlyName = "Camera",
                OwnerEmail = "admin@example.test",
                CatalogBundle = "/srv/catalog.bundle",
                ImageReference = $"sha256:{new string('b', 64)}"
            },
            CancellationToken.None);

        Assert.IsNull(acquired);
    }

    [TestMethod]
    public async Task AcquireImageAsync_SignedRelease_SelectsThisHostPlatformAndVerifiesItsArchive()
    {
        using var fixture = ImageDistributionFixture.Create();
        using var acquirer = new DistributionAcquirer(cacheRoot: fixture.CacheRoot, trustRoot: fixture.TrustRoot);

        var acquired = await acquirer.AcquireImageAsync(fixture.LocalRequest(), CancellationToken.None);

        Assert.IsNotNull(acquired);
        Assert.AreEqual(DistributionAcquirer.HostImageArchitecture(), acquired.Platform.Architecture);
        Assert.AreEqual(fixture.ArchivePathFor(acquired.Platform.Architecture), acquired.ArchivePath);
        Assert.AreEqual(fixture.ImageIdFor(acquired.Platform.Architecture), acquired.Platform.OfflineArchiveImageId);
        Assert.AreEqual("image", acquired.Release.Train);
        Assert.AreEqual("cameraagent-state-v2", acquired.Image.Compatibility.StateContract);
        Assert.AreEqual(fixture.TrustRoot.KeyId, acquired.Evidence.SigningKeyId);
        Assert.AreEqual("verified", acquired.Evidence.VerificationResult);
    }

    [TestMethod]
    public async Task AcquireImageAsync_ReleaseWithoutThisHostArchitecture_NamesWhatItPublishes()
    {
        using var fixture = ImageDistributionFixture.Create(
            publishedArchitectures: [DistributionAcquirer.HostImageArchitecture() == "amd64" ? "arm64" : "amd64"]);
        using var acquirer = new DistributionAcquirer(cacheRoot: fixture.CacheRoot, trustRoot: fixture.TrustRoot);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => acquirer.AcquireImageAsync(fixture.LocalRequest(), CancellationToken.None));

        StringAssert.Contains(exception.Message, "does not support this host", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AcquireImageAsync_ArchiveThatNoLongerMatchesItsSignedIdentity_IsRejected()
    {
        using var fixture = ImageDistributionFixture.Create();
        var archive = fixture.ArchivePathFor(DistributionAcquirer.HostImageArchitecture());
        var bytes = await File.ReadAllBytesAsync(archive);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(archive, bytes);
        using var acquirer = new DistributionAcquirer(cacheRoot: fixture.CacheRoot, trustRoot: fixture.TrustRoot);

        await Assert.ThrowsExactlyAsync<InstallerException>(
            () => acquirer.AcquireImageAsync(fixture.LocalRequest(), CancellationToken.None));
    }

    [TestMethod]
    public async Task AcquireImageAsync_ValidReleaseFromAnotherTrain_IsRejectedAsTheWrongTrain()
    {
        using var fixture = ImageDistributionFixture.Create();
        using var acquirer = new DistributionAcquirer(cacheRoot: fixture.CacheRoot, trustRoot: fixture.TrustRoot);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => acquirer.AcquireImageAsync(fixture.LocalRequest(fixture.ForeignManifestPath), CancellationToken.None));

        StringAssert.Contains(
            exception.InnerException?.Message ?? exception.Message,
            "does not belong to the image train",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void Validate_SignedImageReleaseCombinedWithAnOperatorImage_IsRejected()
    {
        var request = BaseRequest() with
        {
            ImageManifest = "/srv/image-manifest.json",
            ImageReference = $"sha256:{new string('b', 64)}"
        };

        var exception = Assert.ThrowsExactly<InstallUsageException>(request.Validate);

        StringAssert.Contains(exception.Message, "--image-ref cannot be combined", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Validate_SignedImageReleaseWithoutAnOperatorImage_IsAccepted()
    {
        var request = BaseRequest() with { ImageManifest = "/srv/image-manifest.json" };

        request.Validate();
    }

    [TestMethod]
    public void Validate_ImageVersionWithoutAnIndex_IsRejected()
    {
        var request = BaseRequest() with
        {
            ImageManifest = "/srv/image-manifest.json",
            ImageVersion = "1.2.3"
        };

        Assert.ThrowsExactly<InstallUsageException>(request.Validate);
    }

    [TestMethod]
    public async Task PrepareImageAsync_ImageThatCarriesTheSignedCompatibility_IsAccepted()
    {
        using var fixture = ImageDistributionFixture.Create();
        var architecture = DistributionAcquirer.HostImageArchitecture();
        var runner = InspectRunner(fixture.ImageIdFor(architecture), architecture, ImageDistributionFixture.DefaultLabels());

        var result = await new DockerClient(runner).PrepareImageAsync(
            ImageRequest(fixture.ImageIdFor(architecture)), allowMutation: false, fixture.Image, CancellationToken.None);

        Assert.AreEqual("cameraagent-state-v2", result.Image.UpgradeCompatibility);
    }

    [TestMethod]
    public async Task PrepareImageAsync_ImageThatContradictsTheSignedCompatibility_ReportsEveryBoundary()
    {
        using var fixture = ImageDistributionFixture.Create();
        var architecture = DistributionAcquirer.HostImageArchitecture();
        var labels = ImageDistributionFixture.DefaultLabels();
        labels["io.hvo.skymonitor.raw-ingress-schema"] = "11";
        labels["io.hvo.skymonitor.catalog-contract"] = "hyg-v42-production-p3-s1";
        var runner = InspectRunner(fixture.ImageIdFor(architecture), architecture, labels);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => new DockerClient(runner).PrepareImageAsync(
                ImageRequest(fixture.ImageIdFor(architecture)), allowMutation: false, fixture.Image, CancellationToken.None));

        StringAssert.Contains(exception.Message, "raw ingress schema", StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, "catalog contract", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task PrepareImageAsync_ImageThatIsNotTheSignedImage_IsRejectedBeforeCompatibility()
    {
        using var fixture = ImageDistributionFixture.Create();
        var architecture = DistributionAcquirer.HostImageArchitecture();
        var other = $"sha256:{new string('d', 64)}";
        var runner = InspectRunner(other, architecture, ImageDistributionFixture.DefaultLabels());

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => new DockerClient(runner).PrepareImageAsync(
                ImageRequest(other), allowMutation: false, fixture.Image, CancellationToken.None));

        StringAssert.Contains(exception.Message, "not the immutable image the release signed", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task PrepareImageAsync_DaemonThatNamesTheImageByItsManifestDigest_ResolvesTheSignedRelease()
    {
        using var fixture = ImageDistributionFixture.Create();
        var architecture = DistributionAcquirer.HostImageArchitecture();
        var platform = fixture.Image.Platforms.Single(candidate => candidate.Architecture == architecture);
        // The containerd image store knows the manifest digest and not the configuration digest.
        var runner = new StoreScopedProcessRunner(
            architecture,
            platform.ManifestDigest,
            ImageDistributionFixture.DefaultLabels());

        var result = await new DockerClient(runner).PrepareImageAsync(
            ImageRequest(platform.OfflineArchiveImageId!), allowMutation: false, fixture.Image, CancellationToken.None);

        Assert.AreEqual(platform.ManifestDigest, result.Image.ImageId);
        Assert.AreEqual(platform.ManifestDigest, result.Image.ImmutableReference);
    }

    [TestMethod]
    public async Task PrepareImageAsync_DaemonThatKnowsNeitherSignedIdentity_IsRejected()
    {
        using var fixture = ImageDistributionFixture.Create();
        var architecture = DistributionAcquirer.HostImageArchitecture();
        var platform = fixture.Image.Platforms.Single(candidate => candidate.Architecture == architecture);
        var runner = new StoreScopedProcessRunner(
            architecture,
            $"sha256:{new string('f', 64)}",
            ImageDistributionFixture.DefaultLabels());

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => new DockerClient(runner).PrepareImageAsync(
                ImageRequest(platform.OfflineArchiveImageId!), allowMutation: false, fixture.Image, CancellationToken.None));

        StringAssert.Contains(exception.Message, "could not inspect", StringComparison.Ordinal);
    }

    /// <summary>A daemon that knows exactly one image reference, as a specific image store would name it.</summary>
    private sealed class StoreScopedProcessRunner(
        string architecture,
        string knownReference,
        IReadOnlyDictionary<string, string> labels) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (arguments is ["context", "inspect", ..])
            {
                return Task.FromResult(new ProcessResult(0, "unix:///var/run/docker.sock", string.Empty));
            }
            var effective = arguments.Count >= 2 && arguments[0] == "--host" ? arguments.Skip(2).ToArray() : arguments;
            if (effective is ["info", ..])
            {
                return Task.FromResult(new ProcessResult(
                    0,
                    $"{{\"OSType\":\"linux\",\"Architecture\":\"{architecture}\",\"ID\":\"daemon-1\",\"Name\":\"host\",\"ServerVersion\":\"29.0\"}}",
                    string.Empty));
            }
            if (effective is ["compose", ..])
            {
                return Task.FromResult(new ProcessResult(0, "2.40.0", string.Empty));
            }
            if (effective is ["image", "inspect", var reference])
            {
                return reference == knownReference
                    ? Task.FromResult(new ProcessResult(
                        0,
                        JsonSerializer.Serialize(new[]
                        {
                            new
                            {
                                Id = knownReference,
                                Architecture = architecture,
                                Os = "linux",
                                RepoDigests = Array.Empty<string>(),
                                Config = new { Labels = labels }
                            }
                        }),
                        string.Empty))
                    : Task.FromResult(new ProcessResult(1, string.Empty, $"No such image: {reference}"));
            }
            return Task.FromResult(new ProcessResult(1, string.Empty, "unexpected command"));
        }
    }

    private static InstallRequest BaseRequest() => new()
    {
        FriendlyName = "Camera",
        OwnerEmail = "admin@example.test",
        CatalogBundle = "/srv/catalog.bundle",
        ProductRoot = InstallRequest.DefaultProductRoot
    };

    private static InstallRequest ImageRequest(string imageReference) => new()
    {
        FriendlyName = "Camera",
        OwnerEmail = "admin@example.test",
        CatalogBundle = "/srv/catalog.bundle",
        ImageReference = imageReference,
        NoDownload = true
    };

    private static ScriptedProcessRunner InspectRunner(string imageId, string architecture, IReadOnlyDictionary<string, string> labels)
    {
        var labelJson = JsonSerializer.Serialize(labels);
        return new ScriptedProcessRunner(
            $"{{\"OSType\":\"linux\",\"Architecture\":\"{architecture}\",\"ID\":\"daemon-1\",\"Name\":\"host\",\"ServerVersion\":\"29.0\"}}",
            "2.40.0",
            $"[{{\"Id\":\"{imageId}\",\"Architecture\":\"{architecture}\",\"Os\":\"linux\",\"RepoDigests\":[],\"Config\":{{\"Labels\":{labelJson}}}}}]");
    }

    private sealed class ScriptedProcessRunner(params string[] outputs) : IProcessRunner
    {
        private int index;

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (arguments is ["context", "inspect", ..])
            {
                return Task.FromResult(new ProcessResult(0, "unix:///var/run/docker.sock", string.Empty));
            }
            return Task.FromResult(new ProcessResult(0, outputs[index++], string.Empty));
        }
    }

    private sealed class ImageDistributionFixture : IDisposable
    {
        private readonly Dictionary<string, string> archives = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> imageIds = new(StringComparer.Ordinal);

        private ImageDistributionFixture(string root)
        {
            Root = root;
        }

        public string Root { get; }
        public string CacheRoot => Path.Combine(Root, "cache");
        public DistributionTrustRoot TrustRoot { get; private set; } = null!;
        public DistributionImageIdentity Image { get; private set; } = null!;
        public string ManifestPath { get; private set; } = string.Empty;
        public string ForeignManifestPath { get; private set; } = string.Empty;

        public string ArchivePathFor(string architecture) => archives[architecture];

        public string ImageIdFor(string architecture) => imageIds[architecture];

        public static Dictionary<string, string> DefaultLabels() => new(StringComparer.Ordinal)
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
            ["org.opencontainers.image.revision"] = Revision
        };

        public InstallRequest LocalRequest(string? manifestPath = null) => new()
        {
            FriendlyName = "Camera",
            OwnerEmail = "admin@example.test",
            CatalogBundle = "/srv/catalog.bundle",
            ImageManifest = manifestPath ?? ManifestPath,
            NoDownload = true
        };

        public static ImageDistributionFixture Create(IReadOnlyList<string>? publishedArchitectures = null)
        {
            var architectures = publishedArchitectures ?? ["amd64", "arm64"];
            var root = Path.Combine(Path.GetTempPath(), $"hvo-image-distribution-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var fixture = new ImageDistributionFixture(root);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            fixture.TrustRoot = DistributionTrustRoot.FromPem(key.ExportSubjectPublicKeyInfoPem());

            var artifacts = new List<DistributionArtifact>();
            var platforms = new List<DistributionImagePlatform>();
            foreach (var architecture in architectures)
            {
                var assetName = $"cameraagent-image-v1.2.3-linux-{architecture}.tar";
                var path = Path.Combine(root, assetName);
                var content = Encoding.UTF8.GetBytes($"image-archive-{architecture}\n");
                File.WriteAllBytes(path, content);
                var imageId = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(content))}";
                fixture.archives[architecture] = path;
                fixture.imageIds[architecture] = imageId;
                artifacts.Add(new DistributionArtifact(
                    DistributionArtifactRole.ImageArchive, assetName, "application/x-tar", content.Length,
                    Convert.ToHexStringLower(SHA256.HashData(content)), "linux", architecture));
                platforms.Add(new DistributionImagePlatform(
                    "linux",
                    architecture,
                    $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"manifest-{architecture}")))}",
                    assetName,
                    imageId));
            }
            artifacts.Add(Evidence(DistributionArtifactRole.Sbom, "image-sbom.spdx.json"));
            artifacts.Add(Evidence(DistributionArtifactRole.Provenance, "image-provenance.json"));
            artifacts.Add(Evidence(DistributionArtifactRole.VulnerabilityScan, "image-vulnerability-scan.json"));
            artifacts.Add(Evidence(DistributionArtifactRole.License, "THIRD-PARTY-NOTICES.md"));
            artifacts.Add(Evidence(DistributionArtifactRole.Checksums, "SHA256SUMS"));

            fixture.Image = new DistributionImageIdentity(
                "CameraAgent",
                "ghcr.io/roysalisbury/hvo.skymonitor/cameraagent",
                $"sha256:{new string('c', 64)}",
                Revision,
                new string('b', 40),
                platforms,
                "image-provenance.json",
                "image-sbom.spdx.json",
                "image-vulnerability-scan.json",
                new DistributionImageCompatibility(
                    "cameraagent-state-v2", MinimumRevision, "20260827053715_InitialIdentity", 12, 2,
                    "cameraagent-install-v1", "hyg-v42-production-p3-s2", "local-replay-runner-v1"));
            var manifest = new DistributionReleaseManifest(
                DistributionSchemaVersions.ReleaseManifest,
                DistributionManifestKind.ImageRelease,
                new DistributionReleaseIdentity(
                    "image", "1.2.3", "image-v1.2.3", "RoySalisbury/HVO.SkyMonitor", Revision, new string('b', 40),
                    DateTimeOffset.Parse("2026-08-24T00:00:00Z", CultureInfo.InvariantCulture)),
                new DistributionSigningIdentity(DistributionTrustRoot.Algorithm, fixture.TrustRoot.KeyId),
                artifacts,
                null,
                [fixture.Image]);
            fixture.ManifestPath = Write(root, "image-manifest.json", manifest, key);
            // A structurally valid installer release, so the acquirer's train check is what rejects it rather than
            // the verifier refusing a malformed manifest before the check is reached.
            var installerArtifacts = new List<DistributionArtifact>
            {
                Installer("hvo-skymonitor-installer-v1.2.3-linux-x64.tar.gz", "x64"),
                Installer("hvo-skymonitor-installer-v1.2.3-linux-arm64.tar.gz", "arm64"),
                Evidence(DistributionArtifactRole.Sbom, "installer-sbom.spdx.json"),
                Evidence(DistributionArtifactRole.Provenance, "installer-provenance.json"),
                Evidence(DistributionArtifactRole.License, "THIRD-PARTY-NOTICES.md"),
                Evidence(DistributionArtifactRole.Checksums, "SHA256SUMS")
            };
            fixture.ForeignManifestPath = Write(
                root,
                "release-manifest.json",
                manifest with
                {
                    ManifestKind = DistributionManifestKind.InstallerRelease,
                    Release = manifest.Release with { Train = "installer", Tag = "installer-v1.2.3" },
                    Artifacts = installerArtifacts,
                    Images = []
                },
                key);
            return fixture;
        }

        private static DistributionArtifact Installer(string assetName, string architecture)
        {
            var content = Encoding.UTF8.GetBytes(assetName);
            return new DistributionArtifact(
                DistributionArtifactRole.Installer, assetName, "application/gzip", content.Length,
                Convert.ToHexStringLower(SHA256.HashData(content)), "linux", architecture);
        }

        private static DistributionArtifact Evidence(DistributionArtifactRole role, string assetName)
        {
            var content = Encoding.UTF8.GetBytes(assetName);
            return new DistributionArtifact(
                role, assetName, "application/json", content.Length, Convert.ToHexStringLower(SHA256.HashData(content)));
        }

        private static string Write(string root, string name, DistributionReleaseManifest manifest, ECDsa key)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, DistributionJsonContext.Default.DistributionReleaseManifest);
            var path = Path.Combine(root, name);
            File.WriteAllBytes(path, bytes);
            File.WriteAllBytes(
                path + ".sig",
                Encoding.ASCII.GetBytes(Convert.ToBase64String(key.SignData(
                    bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) + "\n"));
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
