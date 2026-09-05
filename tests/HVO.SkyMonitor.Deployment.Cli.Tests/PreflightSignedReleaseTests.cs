using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Deployment.Distribution;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The operator-facing <c>cameraagent preflight</c> command reading its candidate from a signed image release.
/// The command answers whether an upgrade to that release would be accepted, so it resolves the release exactly
/// as the upgrade does while remaining strictly read-only: nothing is pulled, loaded, started, or written,
/// including into the distribution download cache.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class PreflightSignedReleaseTests
{
    private const string CurrentIdentityMigration = "20260827053715_InitialIdentity";
    private const int CurrentRawIngressSchema = 12;
    private const int CurrentCatalogManifestVersion = 2;

    private static readonly uint RuntimeUid = NativeLinux.getuid();
    private static readonly uint RuntimeGid = NativeLinux.getgid();

    private string? previousTestRoot;

    [TestInitialize]
    public void AllowTestProductRoot()
    {
        previousTestRoot = Environment.GetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT");
        Environment.SetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT", "1");
    }

    [TestCleanup]
    public void RestoreTestProductRoot()
        => Environment.SetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT", previousTestRoot);

    [TestMethod]
    public void CommandLine_PreflightSignedRelease_ParsesEveryReleaseSelector()
    {
        var instanceId = Guid.NewGuid();

        var manifestForm = (CameraAgentStatePreflightRequest)CommandLine.ParseCommand(
        [
            "cameraagent", "preflight", "--instance-id", instanceId.ToString("D"),
            "--product-root", "/tmp/hvo-preflight",
            "--image-manifest", "/media/hvo/image-v1.4.0/image-manifest.json", "--json"
        ]);
        Assert.AreEqual(instanceId, manifestForm.InstanceId);
        Assert.AreEqual("/media/hvo/image-v1.4.0/image-manifest.json", manifestForm.ImageManifest);
        Assert.IsNull(manifestForm.ImageReference);
        Assert.IsTrue(manifestForm.NamesSignedImageRelease);
        Assert.IsTrue(manifestForm.Json);

        var indexForm = (CameraAgentStatePreflightRequest)CommandLine.ParseCommand(
        [
            "cameraagent", "preflight", "--instance-id", instanceId.ToString("D"),
            "--product-root", "/tmp/hvo-preflight",
            "--image-index", "https://mirror.example/indexes/image-stable-index.json",
            "--image-version", "1.4.0",
            "--asset-base-url", "https://mirror.example/releases",
            "--channel", "stable"
        ]);
        Assert.AreEqual("https://mirror.example/indexes/image-stable-index.json", indexForm.ImageIndex);
        Assert.AreEqual("1.4.0", indexForm.ImageVersion);
        Assert.AreEqual("https://mirror.example/releases", indexForm.AssetBaseUrl);
        Assert.AreEqual(DistributionChannel.Stable, indexForm.Channel);
    }

    [TestMethod]
    public void CommandLine_PreflightSignedReleaseCombinedWithAnOperatorImage_IsRejected()
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(
        [
            "cameraagent", "preflight", "--instance-id", Guid.NewGuid().ToString("D"),
            "--product-root", "/tmp/hvo-preflight",
            "--image-manifest", "/media/hvo/image-v1.4.0/image-manifest.json",
            "--image-ref", $"sha256:{new string('a', 64)}"
        ]));

        StringAssert.Contains(exception.Message, "--image-ref cannot be combined", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CommandLine_PreflightManifestCombinedWithAnIndex_IsRejected()
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(
        [
            "cameraagent", "preflight", "--instance-id", Guid.NewGuid().ToString("D"),
            "--product-root", "/tmp/hvo-preflight",
            "--image-manifest", "/media/hvo/image-v1.4.0/image-manifest.json",
            "--image-index", "/media/hvo/image-index.json"
        ]));

        StringAssert.Contains(exception.Message, "cannot be combined with --image-index", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CommandLine_PreflightImageVersionWithoutAnIndex_IsRejected()
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(
        [
            "cameraagent", "preflight", "--instance-id", Guid.NewGuid().ToString("D"),
            "--product-root", "/tmp/hvo-preflight", "--image-version", "1.4.0"
        ]));

        StringAssert.Contains(exception.Message, "--image-version requires --image-index", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CommandLine_PreflightHttpsLocatorOnTheLocalChannel_IsRejected()
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(
        [
            "cameraagent", "preflight", "--instance-id", Guid.NewGuid().ToString("D"),
            "--product-root", "/tmp/hvo-preflight",
            "--image-manifest", "https://mirror.example/releases/image-v1.4.0/image-manifest.json"
        ]));

        StringAssert.Contains(exception.Message, "cannot use HTTPS with the local channel", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CommandLine_PreflightChannelWithoutASignedRelease_IsRejected()
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(
        [
            "cameraagent", "preflight", "--instance-id", Guid.NewGuid().ToString("D"),
            "--product-root", "/tmp/hvo-preflight", "--channel", "stable"
        ]));

        StringAssert.Contains(exception.Message, "require --image-manifest or --image-index", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ExecuteAsync_SignedImageRelease_EvaluatesTheSignedBoundariesAndNamesTheRelease()
    {
        using var instance = await InstalledInstanceFixture.CreateCurrentAsync();
        using var release = SignedImageReleaseFixture.Create(
            instance.Root, $"sha256:{new string('c', 64)}", SignedImageReleaseFixture.ContractLabels);

        var report = await CameraAgentStatePreflightManager.ExecuteAsync(
            instance.Request(release.ManifestPath),
            new RefusingProcessRunner(),
            CancellationToken.None,
            release.CreateAcquirer);

        Assert.IsTrue(report.Compatible, CameraAgentStatePreflight.Render(report));
        Assert.AreEqual("image-v1.2.3", report.CandidateRelease);
        Assert.AreEqual($"sha256:{new string('c', 64)}", report.CandidateImageId);
        Assert.AreEqual(CameraAgentStateContract.Current, report.CandidateStateContract);
        Assert.AreEqual(new string('7', 40), report.MinimumCompatibleRevision);
        StringAssert.Contains(
            CameraAgentStatePreflight.Render(report), "Candidate signed release: image-v1.2.3", StringComparison.Ordinal);
    }

    /// <summary>
    /// The signed compatibility record, not the installed image, supplies the candidate boundaries: the same
    /// instance is reported incompatible when the release declares a raw-ingress schema the state cannot serve.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_SignedReleaseDeclaringAnotherRawIngressSchema_IsReportedIncompatible()
    {
        using var instance = await InstalledInstanceFixture.CreateCurrentAsync();
        var drifted = new Dictionary<string, string>(SignedImageReleaseFixture.ContractLabels, StringComparer.Ordinal)
        {
            ["io.hvo.skymonitor.raw-ingress-schema"] = "13"
        };
        using var release = SignedImageReleaseFixture.Create(instance.Root, $"sha256:{new string('c', 64)}", drifted);

        var report = await CameraAgentStatePreflightManager.ExecuteAsync(
            instance.Request(release.ManifestPath),
            new RefusingProcessRunner(),
            CancellationToken.None,
            release.CreateAcquirer);

        Assert.IsFalse(report.Compatible);
        var finding = report.Findings.Single(static value => value.Code == "raw-ingress-schema");
        Assert.IsTrue(finding.Blocking);
        Assert.AreEqual("12", finding.Observed);
        Assert.AreEqual("13", finding.Expected);
    }

    [TestMethod]
    public async Task ExecuteAsync_ReleaseWithoutThisHostArchitecture_NamesWhatItPublishes()
    {
        using var instance = await InstalledInstanceFixture.CreateCurrentAsync();
        using var release = SignedImageReleaseFixture.Create(
            instance.Root,
            $"sha256:{new string('c', 64)}",
            SignedImageReleaseFixture.ContractLabels,
            [DistributionAcquirer.HostImageArchitecture() == "amd64" ? "arm64" : "amd64"]);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => CameraAgentStatePreflightManager.ExecuteAsync(
                instance.Request(release.ManifestPath),
                new RefusingProcessRunner(),
                CancellationToken.None,
                release.CreateAcquirer));

        StringAssert.Contains(exception.Message, "does not support this host", StringComparison.Ordinal);
    }

    /// <summary>
    /// The read-only guarantee: a signed-release preflight creates, modifies, and deletes nothing anywhere under
    /// the product root, the release media, or the distribution cache, and never invokes Docker at all.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_SignedImageRelease_WritesNothingAndNeverInvokesDocker()
    {
        using var instance = await InstalledInstanceFixture.CreateCurrentAsync();
        using var release = SignedImageReleaseFixture.Create(
            instance.Root, $"sha256:{new string('c', 64)}", SignedImageReleaseFixture.ContractLabels);
        var before = Snapshot(instance.Root);
        var runner = new RefusingProcessRunner();

        var report = await CameraAgentStatePreflightManager.ExecuteAsync(
            instance.Request(release.ManifestPath), runner, CancellationToken.None, release.CreateAcquirer);

        Assert.IsTrue(report.Compatible, CameraAgentStatePreflight.Render(report));
        Assert.AreEqual(0, runner.Invocations, "preflight must not pull, load, inspect, or start anything");
        CollectionAssert.AreEqual(before, Snapshot(instance.Root), "preflight must leave every path untouched");
        Assert.IsFalse(
            File.Exists(Path.Combine(instance.Paths.DeploymentStateRoot, "state-preflight.json")),
            "the on-demand preflight retains no report");
    }

    /// <summary>
    /// Resolving a network release reads the same signed metadata an acquisition reads but publishes none of it:
    /// the very same fixture leaves cached metadata behind once the archive is actually acquired.
    /// </summary>
    [TestMethod]
    public async Task ResolveImageAsync_NetworkRelease_LeavesTheDistributionCacheUntouched()
    {
        using var fixture = NetworkImageReleaseFixture.Create();
        using var handler = new FixtureHandler(fixture.NetworkAssets);
        using (var acquirer = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot))
        {
            var resolved = await acquirer.ResolveImageAsync(NetworkImageReleaseFixture.ManifestRequest(), CancellationToken.None);

            Assert.IsNotNull(resolved);
            Assert.AreEqual("image-v1.2.3", resolved.Release.Tag);
            Assert.AreEqual(DistributionAcquirer.HostImageArchitecture(), resolved.Platform.Architecture);
            Assert.AreEqual(fixture.ImageId, resolved.Platform.OfflineArchiveImageId);
            Assert.AreEqual("cameraagent-state-v2", resolved.Image.Compatibility.StateContract);
            Assert.AreEqual(2, handler.RequestCount, "the manifest and its signature are read, and nothing else");
        }
        CollectionAssert.AreEqual(Array.Empty<string>(), Snapshot(fixture.CacheRoot));

        using var acquiringHandler = new FixtureHandler(fixture.NetworkAssets);
        using var acquiring = new DistributionAcquirer(acquiringHandler, fixture.CacheRoot, fixture.TrustRoot);
        _ = await acquiring.AcquireImageAsync(NetworkImageReleaseFixture.ManifestRequest(), CancellationToken.None);

        Assert.IsTrue(
            Snapshot(fixture.CacheRoot).Length > 0,
            "the contrast: an acquisition of the same release does populate the cache");
    }

    /// <summary>
    /// A signed index is rollback-checked without creating the retained state an acquisition commits, so a
    /// preflight can never advance the rollback floor for an upgrade that has not happened.
    /// </summary>
    [TestMethod]
    public async Task ResolveImageAsync_SignedIndexRelease_DoesNotCommitRollbackState()
    {
        using var fixture = NetworkImageReleaseFixture.Create();
        var statePath = Path.Combine(fixture.CacheRoot, "test-state", "image-stable.txt");
        using var handler = new FixtureHandler(fixture.NetworkAssets);
        using (var acquirer = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot))
        {
            var resolved = await acquirer.ResolveImageAsync(NetworkImageReleaseFixture.IndexRequest(), CancellationToken.None);

            Assert.IsNotNull(resolved);
            Assert.AreEqual("1.2.3", resolved.Release.Version);
        }
        Assert.IsFalse(File.Exists(statePath));
        CollectionAssert.AreEqual(Array.Empty<string>(), Snapshot(fixture.CacheRoot));

        using var acquiringHandler = new FixtureHandler(fixture.NetworkAssets);
        using var acquiring = new DistributionAcquirer(acquiringHandler, fixture.CacheRoot, fixture.TrustRoot);
        _ = await acquiring.AcquireImageAsync(NetworkImageReleaseFixture.IndexRequest(), CancellationToken.None);

        Assert.IsTrue(File.Exists(statePath), "the contrast: an acquisition does commit the rollback state");
    }

    /// <summary>Every path beneath a root with its file length, so any creation, deletion, or edit is visible.</summary>
    private static string[] Snapshot(string root)
        => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => File.Exists(path)
                ? $"{Path.GetRelativePath(root, path)}:{new FileInfo(path).Length}"
                : $"{Path.GetRelativePath(root, path)}/")
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>A Docker daemon that must never be contacted.</summary>
    private sealed class RefusingProcessRunner : IProcessRunner
    {
        public int Invocations { get; private set; }

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Invocations++;
            throw new InvalidOperationException(
                $"A read-only preflight must not run: {fileName} {string.Join(' ', arguments)}");
        }
    }

    private sealed class FixtureHandler(IReadOnlyDictionary<Uri, byte[]> responses) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(responses.TryGetValue(request.RequestUri!, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>An installed instance whose persisted state matches the current durable boundaries.</summary>
    private sealed class InstalledInstanceFixture : IDisposable
    {
        private InstalledInstanceFixture(string root, Guid instanceId, InstallationPaths paths)
        {
            Root = root;
            InstanceId = instanceId;
            Paths = paths;
        }

        public string Root { get; }
        public Guid InstanceId { get; }
        public InstallationPaths Paths { get; }

        public CameraAgentStatePreflightRequest Request(string manifestPath)
            => new(InstanceId, Root, null, Json: false) { ImageManifest = manifestPath };

        public static async Task<InstalledInstanceFixture> CreateCurrentAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"hvo-preflight-release-{Guid.NewGuid():N}");
            var instanceId = Guid.NewGuid();
            var paths = InstallationPaths.Create(root, instanceId, ProductionCatalog.CatalogId);
            foreach (var directory in new[]
            {
                paths.ProductRoot, Path.Combine(paths.ProductRoot, "cameraagents"), paths.InstanceRoot,
                paths.ConfigRoot, paths.StateRoot, paths.DeploymentStateRoot, paths.OperationsRoot
            })
            {
                SafeFileSystem.CreateOwnerDirectory(directory);
            }
            var fixture = new InstalledInstanceFixture(root, instanceId, paths);
            fixture.WriteCatalogManifest();
            foreach (var directory in ComposeDeployment.WritableStateDirectories(paths.StateRoot))
            {
                SafeFileSystem.CreateRuntimeDirectory(directory, RuntimeUid, RuntimeGid);
            }
            fixture.WriteIdentityDatabase();
            fixture.WriteRawIngressDatabase();
            await fixture.WriteInstanceManifestAsync().ConfigureAwait(false);
            return fixture;
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root))
            {
                return;
            }
            SafeFileSystem.MakeTreeOwnerWritable(Root);
            Directory.Delete(Root, recursive: true);
        }

        private void WriteCatalogManifest()
        {
            var versionRoot = Path.Combine(Paths.CatalogRoot, "versions", ProductionCatalog.PackageVersion);
            SafeFileSystem.CreateOwnerDirectory(versionRoot);
            File.WriteAllText(
                Path.Combine(versionRoot, "manifest.json"),
                "{\"manifestVersion\":" +
                CurrentCatalogManifestVersion.ToString(CultureInfo.InvariantCulture) +
                ",\"catalog\":{\"id\":\"" + ProductionCatalog.CatalogId + "\"}}");
            Directory.CreateSymbolicLink(
                Path.Combine(Paths.CatalogRoot, "current"), $"versions/{ProductionCatalog.PackageVersion}");
        }

        private void WriteIdentityDatabase()
        {
            var path = CameraAgentStateLayout.IdentityDatabasePath(Paths.StateRoot);
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The fixture composes a fixed lineage row for a private disposable database.
            command.CommandText =
                "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL);" +
                $"INSERT INTO __EFMigrationsHistory VALUES ('{CurrentIdentityMigration}', '10.0.0');";
#pragma warning restore CA2100
            command.ExecuteNonQuery();
        }

        private void WriteRawIngressDatabase()
        {
            var path = CameraAgentStateLayout.RawIngressDatabasePath(Paths.StateRoot);
            SafeFileSystem.CreateOwnerDirectory(Path.GetDirectoryName(path)!);
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            using var command = connection.CreateCommand();
#pragma warning disable CA2100 // PRAGMA user_version cannot be parameterized; the value is a test constant.
            command.CommandText =
                "CREATE TABLE raw_capture (id INTEGER PRIMARY KEY);" +
                $"PRAGMA user_version = {CurrentRawIngressSchema.ToString(CultureInfo.InvariantCulture)};";
#pragma warning restore CA2100
            command.ExecuteNonQuery();
        }

        private Task WriteInstanceManifestAsync()
        {
            var applicationIdentity = Guid.NewGuid();
            var catalog = new CatalogInstallationIdentity(
                ProductionCatalog.CatalogId, ProductionCatalog.PackageVersion, "2", "3",
                ProductionCatalog.DatabaseSha256, ProductionCatalog.DatabaseLength, ProductionCatalog.RowCount,
                Paths.CatalogRoot, new string('a', 64), "local-offline");
            var image = new ImageInstallationIdentity(
                "registry", $"cameraagent@sha256:{new string('b', 64)}", $"sha256:{new string('9', 64)}", "amd64", null,
                UpgradeCompatibility: CameraAgentStateContract.LegacyUnbounded, SourceRevision: new string('8', 40),
                Component: "CameraAgent", ConfigurationContract: "cameraagent-install-v1",
                CatalogContract: "hyg-v42-production-p3-s2");
            var manifest = new InstanceManifest(
                1, "HVO.SkyMonitor", "cameraagent-install-v1", DeploymentComponent.CameraAgent, InstanceId,
                "Preflight Camera", applicationIdentity, $"installer-{InstanceId:D}", 1, new string('d', 64),
                "owner@example.test", 0, 0, 0, "UTC", Guid.NewGuid(), RuntimeUid, RuntimeGid, Paths.ProductRoot,
                Paths.ConfigRoot, Paths.StateRoot, ComposeDeployment.TemplateVersion, new string('e', 64),
                new string('f', 64), new string('1', 64), "default", "1", "1", "active", new string('2', 64),
                new string('3', 64), catalog, image, null, new DockerDaemonIdentity("daemon", "host", "amd64", "29.7.2"),
                CameraAgentStateContract.LegacyUnbounded, DateTimeOffset.UtcNow,
                LifecycleCondition: InstanceLifecycleCondition.Installed);
            return SafeFileSystem.WriteJsonAtomicAsync(
                Paths.ManifestPath, manifest, DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        }
    }

    /// <summary>A signed CameraAgent image release published over HTTPS, both directly and through an index.</summary>
    private sealed class NetworkImageReleaseFixture : IDisposable
    {
        private static readonly Uri ManifestUri =
            new("https://mirror.example/releases/image-v1.2.3/image-manifest.json");
        private static readonly Uri IndexUri = new("https://mirror.example/indexes/image-stable-index.json");

        private NetworkImageReleaseFixture(
            string root, DistributionTrustRoot trustRoot, string imageId, IReadOnlyDictionary<Uri, byte[]> networkAssets)
        {
            Root = root;
            TrustRoot = trustRoot;
            ImageId = imageId;
            NetworkAssets = networkAssets;
        }

        public string Root { get; }
        public string CacheRoot => Path.Combine(Root, "cache");
        public DistributionTrustRoot TrustRoot { get; }
        public string ImageId { get; }
        public IReadOnlyDictionary<Uri, byte[]> NetworkAssets { get; }

        public static InstallRequest ManifestRequest() => new()
        {
            FriendlyName = "Camera",
            OwnerEmail = "admin@example.test",
            CatalogBundle = "/srv/catalog.bundle",
            ImageManifest = ManifestUri.AbsoluteUri,
            Channel = DistributionChannel.Stable
        };

        public static InstallRequest IndexRequest() => ManifestRequest() with
        {
            ImageManifest = null,
            ImageIndex = IndexUri.AbsoluteUri,
            ImageVersion = "1.2.3",
            AssetBaseUrl = "https://mirror.example/releases"
        };

        public static NetworkImageReleaseFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"hvo-preflight-network-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "cache"));
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var trustRoot = DistributionTrustRoot.FromPem(key.ExportSubjectPublicKeyInfoPem());
            var revision = new string('a', 40);
            var network = new Dictionary<Uri, byte[]>();

            var artifacts = new List<DistributionArtifact>();
            var platforms = new List<DistributionImagePlatform>();
            var imageId = $"sha256:{new string('c', 64)}";
            foreach (var architecture in new[] { "amd64", "arm64" })
            {
                var assetName = $"cameraagent-image-v1.2.3-linux-{architecture}.tar";
                var content = Encoding.UTF8.GetBytes($"cameraagent-image-archive-{architecture}\n");
                network[new Uri(ManifestUri, assetName)] = content;
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
            foreach (var (role, assetName) in new[]
                     {
                         (DistributionArtifactRole.Sbom, "image-sbom.spdx.json"),
                         (DistributionArtifactRole.Provenance, "image-provenance.json"),
                         (DistributionArtifactRole.VulnerabilityScan, "image-vulnerability-scan.json"),
                         (DistributionArtifactRole.License, "THIRD-PARTY-NOTICES.md"),
                         (DistributionArtifactRole.Checksums, "SHA256SUMS")
                     })
            {
                var content = Encoding.UTF8.GetBytes(assetName);
                artifacts.Add(new DistributionArtifact(
                    role, assetName, "application/json", content.Length, Convert.ToHexStringLower(SHA256.HashData(content))));
            }

            var manifest = new DistributionReleaseManifest(
                DistributionSchemaVersions.ReleaseManifest,
                DistributionManifestKind.ImageRelease,
                new DistributionReleaseIdentity(
                    "image", "1.2.3", "image-v1.2.3", "RoySalisbury/HVO.SkyMonitor", revision, new string('b', 40),
                    DateTimeOffset.Parse("2026-08-24T00:00:00Z", CultureInfo.InvariantCulture)),
                new DistributionSigningIdentity(DistributionTrustRoot.Algorithm, trustRoot.KeyId),
                artifacts,
                null,
                [
                    new DistributionImageIdentity(
                        "CameraAgent",
                        "ghcr.io/roysalisbury/hvo.skymonitor/cameraagent",
                        $"sha256:{new string('d', 64)}",
                        revision,
                        new string('b', 40),
                        platforms,
                        "image-provenance.json",
                        "image-sbom.spdx.json",
                        "image-vulnerability-scan.json",
                        new DistributionImageCompatibility(
                            "cameraagent-state-v2", new string('7', 40), CurrentIdentityMigration,
                            CurrentRawIngressSchema, CurrentCatalogManifestVersion,
                            "cameraagent-install-v1", "hyg-v42-production-p3-s2", "local-replay-runner-v1"))
                ]);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
                manifest, DistributionJsonContext.Default.DistributionReleaseManifest);
            network[ManifestUri] = manifestBytes;
            network[new Uri(ManifestUri.AbsoluteUri + ".sig")] = Sign(key, manifestBytes);

            var index = new DistributionReleaseIndex(
                DistributionSchemaVersions.ReleaseIndex,
                "release-index",
                "image",
                1,
                DateTimeOffset.Parse("2026-08-24T00:00:00Z", CultureInfo.InvariantCulture),
                "1.2.3",
                new DistributionSigningIdentity(DistributionTrustRoot.Algorithm, trustRoot.KeyId),
                [new DistributionReleaseReference(
                    "1.2.3",
                    "image-v1.2.3",
                    "image-manifest.json",
                    manifestBytes.Length,
                    Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
                    "image-manifest.json.sig")]);
            var indexBytes = JsonSerializer.SerializeToUtf8Bytes(
                index, DistributionJsonContext.Default.DistributionReleaseIndex);
            network[IndexUri] = indexBytes;
            network[new Uri(IndexUri.AbsoluteUri + ".sig")] = Sign(key, indexBytes);
            return new NetworkImageReleaseFixture(root, trustRoot, imageId, network);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static byte[] Sign(ECDsa key, byte[] bytes)
            => Encoding.ASCII.GetBytes(Convert.ToBase64String(key.SignData(
                bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) + "\n");
    }
}
