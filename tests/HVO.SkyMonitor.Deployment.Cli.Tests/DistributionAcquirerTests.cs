using System.Formats.Tar;
using System.IO.Compression;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Deployment.Distribution;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class DistributionAcquirerTests
{
    [TestMethod]
    public async Task AcquireAsync_SignedOfflineBundle_VerifiesAndExtractsExactFiles()
    {
        using var fixture = CatalogDistributionFixture.Create();
        using var acquirer = new DistributionAcquirer(cacheRoot: fixture.CacheRoot, trustRoot: fixture.TrustRoot);

        using var acquired = await acquirer.AcquireAsync(fixture.LocalRequest(), CancellationToken.None);

        CollectionAssert.AreEquivalent(fixture.CatalogFileNames, Directory.GetFiles(acquired.BundlePath).Select(Path.GetFileName).ToArray());
        Assert.AreEqual(fixture.AssetSha256, acquired.Evidence?.AssetSha256);
        Assert.AreEqual(fixture.TrustRoot.KeyId, acquired.Evidence?.SigningKeyId);
    }

    [TestMethod]
    public async Task AcquireAsync_VerifiedNetworkCache_SupportsNoDownloadReuse()
    {
        using var fixture = CatalogDistributionFixture.Create();
        using var handler = new FixtureHandler(fixture.NetworkAssets);
        using (var online = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot))
        using (var acquired = await online.AcquireAsync(fixture.NetworkRequest(noDownload: false), CancellationToken.None))
        {
            Assert.AreEqual(3, handler.RequestCount);
        }

        using var offlineHandler = new FixtureHandler(new Dictionary<Uri, byte[]>());
        using var offline = new DistributionAcquirer(offlineHandler, fixture.CacheRoot, fixture.TrustRoot);
        using var cached = await offline.AcquireAsync(fixture.NetworkRequest(noDownload: true), CancellationToken.None);

        Assert.AreEqual(0, offlineHandler.RequestCount);
        Assert.AreEqual(fixture.AssetSha256, cached.Evidence?.AssetSha256);
    }

    [TestMethod]
    public async Task AcquireAsync_CorruptMirroredAsset_IsRejectedAndNotPublishedToCache()
    {
        using var fixture = CatalogDistributionFixture.Create();
        var assets = fixture.NetworkAssets.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToArray());
        assets[fixture.BundleUri][^1] ^= 1;
        using var handler = new FixtureHandler(assets);
        using var acquirer = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot);

        await Assert.ThrowsExactlyAsync<InstallerException>(
            () => acquirer.AcquireAsync(fixture.NetworkRequest(noDownload: false), CancellationToken.None));

        Assert.IsFalse(Directory.EnumerateFiles(fixture.CacheRoot, fixture.AssetName, SearchOption.AllDirectories).Any());
    }

    [TestMethod]
    public async Task AcquireAsync_SignedIndexDefault_ResolvesImmutableReleaseAssets()
    {
        using var fixture = CatalogDistributionFixture.Create();
        using var handler = new FixtureHandler(fixture.NetworkAssets);
        using var acquirer = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot);

        using var acquired = await acquirer.AcquireAsync(fixture.IndexRequest(), CancellationToken.None);

        Assert.AreEqual(fixture.AssetSha256, acquired.Evidence?.AssetSha256);
        Assert.AreEqual(5, handler.RequestCount);
    }

    [TestMethod]
    public async Task AcquireAsync_ApprovedGitHubCdnRedirect_RecordsTerminalUri()
    {
        using var fixture = CatalogDistributionFixture.Create();
        var source = new Uri($"https://github.com/RoySalisbury/HVO.SkyMonitor/releases/download/catalog-fixture/{fixture.AssetName}");
        var terminal = new Uri("https://release-assets.githubusercontent.com/immutable/catalog-bundle.tar.gz");
        var assets = fixture.NetworkAssets.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        assets[source] = assets[fixture.BundleUri];
        using var handler = new RedirectFixtureHandler(assets, source, terminal);
        using var acquirer = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot);

        var request = fixture.NetworkRequest(noDownload: false) with
        {
            AssetBaseUrl = "https://github.com/RoySalisbury/HVO.SkyMonitor/releases/download/catalog-fixture"
        };
        using var acquired = await acquirer.AcquireAsync(request, CancellationToken.None);

        Assert.AreEqual(terminal, acquired.Evidence?.ResolvedPublicUri);
    }

    [TestMethod]
    public async Task AcquireAsync_CrossOriginRedirect_IsRejected()
    {
        using var fixture = CatalogDistributionFixture.Create();
        using var handler = new RedirectFixtureHandler(
            fixture.NetworkAssets, fixture.BundleUri, new Uri("https://untrusted.example/catalog-bundle.tar.gz"));
        using var acquirer = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot);

        await Assert.ThrowsExactlyAsync<InstallerException>(
            () => acquirer.AcquireAsync(fixture.NetworkRequest(noDownload: false), CancellationToken.None));
    }

    [TestMethod]
    public async Task AcquireAsync_PrecreatedPartialSymlink_IsRejectedWithoutChangingTarget()
    {
        using var fixture = CatalogDistributionFixture.Create();
        var assetRoot = Path.Combine(fixture.CacheRoot, "v1", "assets", "sha256", fixture.AssetSha256[..2], fixture.AssetSha256);
        Directory.CreateDirectory(assetRoot);
        var victim = Path.Combine(fixture.Root, "victim");
        await File.WriteAllTextAsync(victim, "unchanged");
        File.SetUnixFileMode(victim, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.CreateSymbolicLink(Path.Combine(assetRoot, $".{fixture.AssetName}.partial"), victim);
        using var handler = new FixtureHandler(fixture.NetworkAssets);
        using var acquirer = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot);

        await Assert.ThrowsExactlyAsync<InstallerException>(
            () => acquirer.AcquireAsync(fixture.NetworkRequest(noDownload: false), CancellationToken.None));

        Assert.AreEqual("unchanged", await File.ReadAllTextAsync(victim));
    }

    [TestMethod]
    public async Task AcquireAsync_FailedIndexedAsset_DoesNotAdvanceRollbackState()
    {
        using var fixture = CatalogDistributionFixture.Create();
        var assets = fixture.NetworkAssets.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToArray());
        assets[fixture.BundleUri][^1] ^= 1;
        using var handler = new FixtureHandler(assets);
        using var acquirer = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot);

        await Assert.ThrowsExactlyAsync<InstallerException>(
            () => acquirer.AcquireAsync(fixture.IndexRequest(), CancellationToken.None));

        Assert.IsFalse(File.Exists(Path.Combine(fixture.CacheRoot, "test-state", "catalog-stable.txt")));
    }

    private class FixtureHandler(IReadOnlyDictionary<Uri, byte[]> responses) : HttpMessageHandler
    {
        protected IReadOnlyDictionary<Uri, byte[]> Responses { get; } = responses;

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            if (!Responses.TryGetValue(request.RequestUri!, out var bytes))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            return Task.FromResult(response);
        }
    }

    private sealed class RedirectFixtureHandler(
        IReadOnlyDictionary<Uri, byte[]> responses,
        Uri redirectSource,
        Uri redirectTarget) : FixtureHandler(responses)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri == redirectSource)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = redirectTarget }
                });
            }
            if (request.RequestUri == redirectTarget && Responses.TryGetValue(redirectSource, out var bytes))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class CatalogDistributionFixture : IDisposable
    {
        private CatalogDistributionFixture(
            string root,
            DistributionTrustRoot trustRoot,
            string manifestPath,
            string bundlePath,
            string assetName,
            string assetSha256,
            IReadOnlyDictionary<Uri, byte[]> networkAssets,
            Uri bundleUri,
            Uri indexUri)
        {
            Root = root;
            TrustRoot = trustRoot;
            ManifestPath = manifestPath;
            BundlePath = bundlePath;
            AssetName = assetName;
            AssetSha256 = assetSha256;
            NetworkAssets = networkAssets;
            BundleUri = bundleUri;
            IndexUri = indexUri;
        }

        public string Root { get; }
        public string CacheRoot => Path.Combine(Root, "cache");
        public DistributionTrustRoot TrustRoot { get; }
        public string ManifestPath { get; }
        public string BundlePath { get; }
        public string AssetName { get; }
        public string AssetSha256 { get; }
        public IReadOnlyDictionary<Uri, byte[]> NetworkAssets { get; }
        public Uri BundleUri { get; }
        public Uri IndexUri { get; }
        public string[] CatalogFileNames { get; } = ["manifest.json", "hyg_v42.sqlite", "LICENSE-HYG.md", "ATTRIBUTION-HYG.md"];

        public static CatalogDistributionFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"hvo-catalog-distribution-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var bundleFiles = Path.Combine(root, "bundle-files");
            Directory.CreateDirectory(bundleFiles);
            var databaseBytes = "sqlite-fixture"u8.ToArray();
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                manifestVersion = 2,
                package = new { kind = "production", version = "hyg-v4.2-p3-s2-r1" },
                catalog = new { id = "hyg-v42-production" },
                schemaVersion = "2",
                preprocessingVersion = "3",
                database = new { sha256 = Hash(databaseBytes), length = databaseBytes.Length, rowCount = 1 },
                license = new
                {
                    identifier = "CC-BY-SA-4.0",
                    file = new { relativePath = "LICENSE-HYG.md" },
                    attribution = new { relativePath = "ATTRIBUTION-HYG.md" }
                },
                topology = new { identity = "fixture-topology", sha256 = new string('e', 64) }
            });
            File.WriteAllBytes(Path.Combine(bundleFiles, "manifest.json"), manifestBytes);
            File.WriteAllBytes(Path.Combine(bundleFiles, "hyg_v42.sqlite"), databaseBytes);
            File.WriteAllText(Path.Combine(bundleFiles, "LICENSE-HYG.md"), "license\n");
            File.WriteAllText(Path.Combine(bundleFiles, "ATTRIBUTION-HYG.md"), "attribution\n");
            const string assetName = "hyg-v4.2-p3-s2-r1.bundle.tar.gz";
            var bundlePath = Path.Combine(root, assetName);
            WriteArchive(bundleFiles, bundlePath);
            var bundleBytes = File.ReadAllBytes(bundlePath);
            var assetSha256 = Hash(bundleBytes);

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var trustRoot = DistributionTrustRoot.FromPem(key.ExportSubjectPublicKeyInfoPem());
            var catalog = new DistributionCatalogIdentity(
                "hyg-v42-production", "hyg-v4.2-p3-s2-r1", "production", 2, "2", "3",
                Hash(manifestBytes), Hash(databaseBytes), databaseBytes.Length, 1, "CC-BY-SA-4.0",
                "LICENSE-HYG.md", "ATTRIBUTION-HYG.md", "fixture-topology", new string('e', 64));
            var artifacts = new[]
            {
                Artifact(DistributionArtifactRole.CatalogBundle, assetName, bundleBytes),
                Artifact(DistributionArtifactRole.Checksums, "SHA256SUMS", "checksums"u8.ToArray()),
                Artifact(DistributionArtifactRole.Sbom, "catalog-sbom.spdx.json", "sbom"u8.ToArray()),
                Artifact(DistributionArtifactRole.Provenance, "catalog-provenance.json", "provenance"u8.ToArray()),
                Artifact(DistributionArtifactRole.License, "LICENSE-HYG.md", "license"u8.ToArray()),
                Artifact(DistributionArtifactRole.Attribution, "ATTRIBUTION-HYG.md", "attribution"u8.ToArray())
            };
            var release = new DistributionReleaseManifest(
                DistributionSchemaVersions.ReleaseManifest,
                DistributionManifestKind.CatalogRelease,
                new DistributionReleaseIdentity(
                    "catalog", catalog.PackageVersion, $"catalog-{catalog.PackageVersion}", "RoySalisbury/HVO.SkyMonitor",
                    new string('a', 40), new string('b', 40), DateTimeOffset.Parse("2026-08-24T00:00:00Z", CultureInfo.InvariantCulture)),
                new DistributionSigningIdentity(DistributionTrustRoot.Algorithm, trustRoot.KeyId), artifacts, catalog, []);
            var releaseBytes = JsonSerializer.SerializeToUtf8Bytes(release, DistributionJsonContext.Default.DistributionReleaseManifest);
            var signature = Encoding.ASCII.GetBytes(Convert.ToBase64String(key.SignData(
                releaseBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) + "\n");
            var manifestPath = Path.Combine(root, "catalog-manifest.json");
            File.WriteAllBytes(manifestPath, releaseBytes);
            File.WriteAllBytes(manifestPath + ".sig", signature);

            var manifestUri = new Uri("https://mirror.example/releases/catalog-hyg-v4.2-p3-s2-r1/catalog-manifest.json");
            var bundleUri = new Uri(manifestUri, assetName);
            var network = new Dictionary<Uri, byte[]>
            {
                [manifestUri] = releaseBytes,
                [new Uri(manifestUri.AbsoluteUri + ".sig")] = signature,
                [bundleUri] = bundleBytes
            };
            var index = new DistributionReleaseIndex(
                DistributionSchemaVersions.ReleaseIndex,
                "release-index",
                "catalog",
                1,
                DateTimeOffset.Parse("2026-08-24T00:00:00Z", CultureInfo.InvariantCulture),
                catalog.PackageVersion,
                new DistributionSigningIdentity(DistributionTrustRoot.Algorithm, trustRoot.KeyId),
                [new DistributionReleaseReference(
                    catalog.PackageVersion,
                    $"catalog-{catalog.PackageVersion}",
                    "catalog-manifest.json",
                    releaseBytes.Length,
                    Hash(releaseBytes),
                    "catalog-manifest.json.sig")]);
            var indexBytes = JsonSerializer.SerializeToUtf8Bytes(index, DistributionJsonContext.Default.DistributionReleaseIndex);
            var indexSignature = Encoding.ASCII.GetBytes(Convert.ToBase64String(key.SignData(
                indexBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) + "\n");
            var indexUri = new Uri("https://mirror.example/indexes/catalog-stable-index.json");
            network[indexUri] = indexBytes;
            network[new Uri(indexUri.AbsoluteUri + ".sig")] = indexSignature;
            return new CatalogDistributionFixture(
                root, trustRoot, manifestPath, bundlePath, assetName, assetSha256, network, bundleUri, indexUri);
        }

        public InstallRequest LocalRequest() => Request(ManifestPath, BundlePath, DistributionChannel.Local, noDownload: true);

        public InstallRequest NetworkRequest(bool noDownload)
            => Request(NetworkAssets.Keys.Single(static uri => uri.AbsolutePath.EndsWith("catalog-manifest.json", StringComparison.Ordinal)).AbsoluteUri,
                null, DistributionChannel.Stable, noDownload);

        public InstallRequest IndexRequest()
            => Request(IndexUri.AbsoluteUri, null, DistributionChannel.Stable, noDownload: false) with
            {
                CatalogManifest = null,
                CatalogIndex = IndexUri.AbsoluteUri,
                AssetBaseUrl = "https://mirror.example/releases"
            };

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static InstallRequest Request(string manifest, string? bundle, DistributionChannel channel, bool noDownload)
            => new()
            {
                FriendlyName = "Fixture",
                OwnerEmail = "owner@example.test",
                CatalogManifest = manifest,
                CatalogBundle = bundle,
                Channel = channel,
                ImageReference = $"sha256:{new string('f', 64)}",
                NoDownload = noDownload
            };

        private static DistributionArtifact Artifact(DistributionArtifactRole role, string name, byte[] bytes)
            => new(role, name, "application/octet-stream", bytes.Length, Hash(bytes));

        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

        private static void WriteArchive(string sourceRoot, string output)
        {
            using var file = File.Create(output);
            using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
            using var writer = new TarWriter(gzip, TarEntryFormat.Ustar);
            foreach (var name in new[] { "ATTRIBUTION-HYG.md", "LICENSE-HYG.md", "hyg_v42.sqlite", "manifest.json" })
            {
                using var input = File.OpenRead(Path.Combine(sourceRoot, name));
                writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, $"hyg-v4.2-p3-s2-r1.bundle/{name}")
                {
                    DataStream = input,
                    ModificationTime = DateTimeOffset.UnixEpoch
                });
            }
        }
    }
}
