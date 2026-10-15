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
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AcquireAsync_SignedOfflineBundle_VerifiesAndExtractsExactFiles()
    {
        using var fixture = CatalogDistributionFixture.Create();
        using var acquirer = new DistributionAcquirer(
            cacheRoot: fixture.CacheRoot, trustRoot: fixture.TrustRoot, catalogSpecifications: fixture.CatalogSpecifications);

        using var acquired = await acquirer.AcquireAsync(fixture.LocalRequest(), CancellationToken.None);

        CollectionAssert.AreEquivalent(fixture.CatalogFileNames, Directory.GetFiles(acquired.BundlePath).Select(Path.GetFileName).ToArray());
        Assert.AreEqual(fixture.AssetSha256, acquired.Evidence?.AssetSha256);
        Assert.AreEqual(fixture.TrustRoot.KeyId, acquired.Evidence?.SigningKeyId);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AcquireAsync_VerifiedNetworkCache_SupportsNoDownloadReuse()
    {
        using var fixture = CatalogDistributionFixture.Create();
        using var handler = new FixtureHandler(fixture.NetworkAssets);
        using (var online = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot, catalogSpecifications: fixture.CatalogSpecifications))
        using (var acquired = await online.AcquireAsync(fixture.NetworkRequest(noDownload: false), CancellationToken.None))
        {
            Assert.AreEqual(3, handler.RequestCount);
        }

        using var offlineHandler = new FixtureHandler(new Dictionary<Uri, byte[]>());
        using var offline = new DistributionAcquirer(offlineHandler, fixture.CacheRoot, fixture.TrustRoot, catalogSpecifications: fixture.CatalogSpecifications);
        using var cached = await offline.AcquireAsync(fixture.NetworkRequest(noDownload: true), CancellationToken.None);

        Assert.AreEqual(0, offlineHandler.RequestCount);
        Assert.AreEqual(fixture.AssetSha256, cached.Evidence?.AssetSha256);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AcquireAsync_CorruptMirroredAsset_IsRejectedAndNotPublishedToCache()
    {
        using var fixture = CatalogDistributionFixture.Create();
        var assets = fixture.NetworkAssets.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToArray());
        assets[fixture.BundleUri][^1] ^= 1;
        using var handler = new FixtureHandler(assets);
        using var acquirer = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot, catalogSpecifications: fixture.CatalogSpecifications);

        await Assert.ThrowsExactlyAsync<InstallerException>(
            () => acquirer.AcquireAsync(fixture.NetworkRequest(noDownload: false), CancellationToken.None));

        Assert.IsFalse(Directory.EnumerateFiles(fixture.CacheRoot, fixture.AssetName, SearchOption.AllDirectories).Any());
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AcquireAsync_SignedIndexDefault_ResolvesImmutableReleaseAssets()
    {
        using var fixture = CatalogDistributionFixture.Create();
        using var handler = new FixtureHandler(fixture.NetworkAssets);
        using var acquirer = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot, catalogSpecifications: fixture.CatalogSpecifications);

        using var acquired = await acquirer.AcquireAsync(fixture.IndexRequest(), CancellationToken.None);

        Assert.AreEqual(fixture.AssetSha256, acquired.Evidence?.AssetSha256);
        Assert.AreEqual(5, handler.RequestCount);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AcquireAsync_ApprovedGitHubCdnRedirect_RecordsTerminalUri()
    {
        using var fixture = CatalogDistributionFixture.Create();
        var source = new Uri($"https://github.com/RoySalisbury/HVO.SkyMonitor/releases/download/catalog-fixture/{fixture.AssetName}");
        var terminal = new Uri("https://release-assets.githubusercontent.com/immutable/catalog-bundle.tar.gz");
        var assets = fixture.NetworkAssets.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        assets[source] = assets[fixture.BundleUri];
        using var handler = new RedirectFixtureHandler(assets, source, terminal);
        using var acquirer = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot, catalogSpecifications: fixture.CatalogSpecifications);

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
        using var acquirer = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot, catalogSpecifications: fixture.CatalogSpecifications);

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
        using var acquirer = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot, catalogSpecifications: fixture.CatalogSpecifications);

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
        using var acquirer = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot, catalogSpecifications: fixture.CatalogSpecifications);

        await Assert.ThrowsExactlyAsync<InstallerException>(
            () => acquirer.AcquireAsync(fixture.IndexRequest(), CancellationToken.None));

        Assert.IsFalse(File.Exists(Path.Combine(fixture.CacheRoot, "test-state", "catalog-stable.txt")));
    }

    [TestMethod]
    public async Task AcquireAsync_StalledMetadataDownload_IsReportedAsDistributionFailureNotCancellation()
    {
        using var fixture = CatalogDistributionFixture.Create();
        var manifestUri = fixture.NetworkAssets.Keys.Single(static uri => uri.AbsolutePath.EndsWith("catalog-manifest.json", StringComparison.Ordinal));
        using var handler = new StallingHandler(fixture.NetworkAssets, manifestUri);
        using var acquirer = new DistributionAcquirer(
            handler, fixture.CacheRoot, fixture.TrustRoot, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMinutes(30),
            fixture.CatalogSpecifications);
        using var caller = new CancellationTokenSource();

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => acquirer.AcquireAsync(fixture.NetworkRequest(noDownload: false), caller.Token));

        // The stall is attributed to the distribution source, after every attempt, and never to the operator.
        StringAssert.Contains(exception.Message, "stalled", StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, manifestUri.Host, StringComparison.Ordinal);
        Assert.AreEqual(3, handler.StalledRequests);
        Assert.IsFalse(caller.IsCancellationRequested);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AcquireAsync_StalledAssetDownload_IsReportedAsDistributionFailureNotCancellation()
    {
        using var fixture = CatalogDistributionFixture.Create();
        using var handler = new StallingHandler(fixture.NetworkAssets, fixture.BundleUri);
        using var acquirer = new DistributionAcquirer(
            handler, fixture.CacheRoot, fixture.TrustRoot, null, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250),
            fixture.CatalogSpecifications);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => acquirer.AcquireAsync(fixture.NetworkRequest(noDownload: false), CancellationToken.None));

        StringAssert.Contains(exception.Message, "stalled", StringComparison.Ordinal);
        Assert.AreEqual(3, handler.StalledRequests);
        Assert.IsFalse(Directory.EnumerateFiles(fixture.CacheRoot, fixture.AssetName, SearchOption.AllDirectories).Any());
    }

    [TestMethod]
    public async Task AcquireAsync_CallerCancellationDuringStall_StillPropagatesAsCancellation()
    {
        using var fixture = CatalogDistributionFixture.Create();
        var manifestUri = fixture.NetworkAssets.Keys.Single(static uri => uri.AbsolutePath.EndsWith("catalog-manifest.json", StringComparison.Ordinal));
        using var handler = new StallingHandler(fixture.NetworkAssets, manifestUri);
        using var acquirer = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot, catalogSpecifications: fixture.CatalogSpecifications);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => acquirer.AcquireAsync(fixture.NetworkRequest(noDownload: false), caller.Token));

        // The operator's cancellation is not retried and is not rewritten as a distribution failure.
        Assert.AreEqual(1, handler.StalledRequests);
    }

    /// <summary>
    /// Serves every fixture asset normally except one URI, whose request never completes until the token the
    /// acquirer hands the handler fires, exactly like a mirror that accepts the connection and then goes quiet.
    /// </summary>
    private sealed class StallingHandler(IReadOnlyDictionary<Uri, byte[]> responses, Uri stalledUri) : FixtureHandler(responses)
    {
        public int StalledRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri == stalledUri)
            {
                StalledRequests++;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }

    [TestMethod]
    public async Task ResolveCatalogAsync_LocalManifest_ReturnsSignedIdentityWithoutCacheWrites()
    {
        using var fixture = CatalogDistributionFixture.Create();
        using var acquirer = new DistributionAcquirer(
            cacheRoot: fixture.CacheRoot, trustRoot: fixture.TrustRoot, catalogSpecifications: fixture.CatalogSpecifications);

        var resolved = await acquirer.ResolveCatalogAsync(fixture.LocalRequest(), CancellationToken.None);

        Assert.IsNotNull(resolved);
        Assert.AreEqual("hyg-v42-production", resolved.Catalog.CatalogId);
        Assert.AreEqual("hyg-v4.2-p3-s2-r1", resolved.Catalog.PackageVersion);
        Assert.AreEqual("catalog-hyg-v4.2-p3-s2-r1", resolved.Release.Tag);
        AssertNoCacheEntries(fixture);
    }

    [TestMethod]
    public async Task ResolveCatalogAsync_NetworkManifest_VerifiesSignatureWithoutDownloadingTheBundle()
    {
        using var fixture = CatalogDistributionFixture.Create();
        using var handler = new FixtureHandler(fixture.NetworkAssets);
        using var acquirer = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot, catalogSpecifications: fixture.CatalogSpecifications);

        var resolved = await acquirer.ResolveCatalogAsync(fixture.NetworkRequest(noDownload: false), CancellationToken.None);

        Assert.AreEqual("hyg-v4.2-p3-s2-r1", resolved?.Catalog.PackageVersion);
        Assert.AreEqual(2, handler.RequestCount);
        AssertNoCacheEntries(fixture);
    }

    [TestMethod]
    public async Task ResolveCatalogAsync_SignedIndex_DoesNotCommitIndexRollbackState()
    {
        using var fixture = CatalogDistributionFixture.Create();
        using var handler = new FixtureHandler(fixture.NetworkAssets);
        using var acquirer = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot, catalogSpecifications: fixture.CatalogSpecifications);

        var resolved = await acquirer.ResolveCatalogAsync(fixture.IndexRequest(), CancellationToken.None);

        Assert.AreEqual("hyg-v4.2-p3-s2-r1", resolved?.Catalog.PackageVersion);
        Assert.AreEqual(4, handler.RequestCount);
        AssertNoCacheEntries(fixture);
    }

    [TestMethod]
    public async Task ResolveCatalogAsync_OfflineWithoutVerifiedCache_FailsClosedWithoutNetwork()
    {
        using var fixture = CatalogDistributionFixture.Create();
        using var handler = new FixtureHandler(fixture.NetworkAssets);
        using var acquirer = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot, catalogSpecifications: fixture.CatalogSpecifications);

        await Assert.ThrowsExactlyAsync<InstallerException>(
            () => acquirer.ResolveCatalogAsync(fixture.NetworkRequest(noDownload: true), CancellationToken.None));

        Assert.AreEqual(0, handler.RequestCount);
        AssertNoCacheEntries(fixture);
    }

    [TestMethod]
    public async Task ResolveCatalogAsync_WithoutSignedLocator_ReturnsNull()
    {
        using var fixture = CatalogDistributionFixture.Create();
        using var acquirer = new DistributionAcquirer(
            cacheRoot: fixture.CacheRoot, trustRoot: fixture.TrustRoot, catalogSpecifications: fixture.CatalogSpecifications);

        Assert.IsNull(await acquirer.ResolveCatalogAsync(
            fixture.LocalRequest() with { CatalogManifest = null }, CancellationToken.None));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CatalogCheck_UninstalledPackage_ReportsAvailableAndWritesNothing()
    {
        using var fixture = CatalogDistributionFixture.Create();
        using var handler = new FixtureHandler(fixture.NetworkAssets);
        var productRoot = Path.Combine(fixture.Root, "product");

        var result = await CheckAsync(fixture, handler, productRoot);

        Assert.AreEqual(LifecycleOperationKind.CatalogCheck, result.Operation);
        Assert.AreEqual("available", result.Outcome);
        Assert.AreEqual("hyg-v4.2-p3-s2-r1", result.Catalog?.PackageVersion);
        Assert.AreEqual("signed-release", result.Catalog?.Source);
        Assert.IsNull(result.InstanceId);
        Assert.AreEqual(2, handler.RequestCount);
        Assert.IsFalse(Directory.Exists(productRoot));
        AssertNoCacheEntries(fixture);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CatalogCheck_InstalledSignedPackage_ReportsTheRetainedIdentityAndChangesNothing()
    {
        // Environment-gated like the lifecycle transitions: proving an installed package needs the real bundle.
        var bundle = RequireProductionBundle();
        var productRoot = Path.Combine(Path.GetTempPath(), $"hvo-catalog-check-{Guid.NewGuid():N}");
        try
        {
            var installed = await InstallProductionPackageAsync(bundle, productRoot);
            using var fixture = CatalogDistributionFixture.Create(
                signProductionDatabaseIdentity: true, signedBundleManifestSha256: installed.ManifestSha256);
            using var handler = new FixtureHandler(fixture.NetworkAssets);
            var before = Snapshot(productRoot);

            var result = await CheckAsync(fixture, handler, productRoot);

            Assert.AreEqual("installed", result.Outcome);
            Assert.AreEqual(installed, result.Catalog);
            CollectionAssert.AreEqual(before, Snapshot(productRoot));
            AssertNoCacheEntries(fixture);
        }
        finally
        {
            DeleteWritable(productRoot);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CatalogCheck_RetainedRecordWithoutInstalledPayload_IsRefused()
    {
        // A matching installed.json over an empty version directory is a record, not an installed package.
        using var fixture = CatalogDistributionFixture.Create(signProductionDatabaseIdentity: true);
        using var handler = new FixtureHandler(fixture.NetworkAssets);
        var productRoot = Path.Combine(fixture.Root, "product");
        var signed = await ResolveSignedCatalogAsync(fixture);
        InstallRetainedIdentity(productRoot, signed.BundleManifestSha256);
        var before = Snapshot(productRoot);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CheckAsync(fixture, handler, productRoot));

        Assert.AreEqual("The installed catalog version is missing, corrupt, or not the approved package.", exception.Message);
        CollectionAssert.AreEqual(before, Snapshot(productRoot));
        AssertNoCacheEntries(fixture);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("corrupt-database")]
    [DataRow("truncated-database")]
    [DataRow("wrong-lineage-manifest")]
    public async Task CatalogCheck_DamagedOrSubstitutedInstalledPayload_IsRefused(string damage)
    {
        var bundle = RequireProductionBundle();
        var productRoot = Path.Combine(Path.GetTempPath(), $"hvo-catalog-check-{Guid.NewGuid():N}");
        try
        {
            var installed = await InstallProductionPackageAsync(bundle, productRoot);
            using var fixture = CatalogDistributionFixture.Create(
                signProductionDatabaseIdentity: true, signedBundleManifestSha256: installed.ManifestSha256);
            using var handler = new FixtureHandler(fixture.NetworkAssets);
            var versionRoot = Path.Combine(installed.InstallRoot, "versions", installed.PackageVersion);
            var target = Path.Combine(versionRoot, damage == "wrong-lineage-manifest" ? "manifest.json" : "hyg_v42.sqlite");
            File.SetUnixFileMode(versionRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            switch (damage)
            {
                case "corrupt-database":
                    var bytes = await File.ReadAllBytesAsync(target);
                    bytes[bytes.Length / 2] ^= 0xFF;
                    await File.WriteAllBytesAsync(target, bytes);
                    break;
                case "truncated-database":
                    await using (var stream = File.Open(target, FileMode.Open, FileAccess.Write))
                        stream.SetLength(stream.Length / 2);
                    break;
                default:
                    // The retained record still names HYG 4.2, but the installed manifest now claims the 4.4 lineage.
                    var manifest = await File.ReadAllTextAsync(target);
                    await File.WriteAllTextAsync(target, manifest.Replace(
                        "\"hyg-v42-production\"", "\"hyg-v44-production\"", StringComparison.Ordinal));
                    break;
            }
            var before = Snapshot(productRoot);

            var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CheckAsync(fixture, handler, productRoot));

            Assert.AreEqual("The installed catalog version is missing, corrupt, or not the approved package.", exception.Message);
            CollectionAssert.AreEqual(before, Snapshot(productRoot));
            AssertNoCacheEntries(fixture);
        }
        finally
        {
            DeleteWritable(productRoot);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CatalogCheck_InstalledBytesDifferingFromTheSignedIdentity_AreRefused()
    {
        // A valid installed package whose bundle manifest is not the one the release signed is refused.
        var bundle = RequireProductionBundle();
        var productRoot = Path.Combine(Path.GetTempPath(), $"hvo-catalog-check-{Guid.NewGuid():N}");
        try
        {
            await InstallProductionPackageAsync(bundle, productRoot);
            using var fixture = CatalogDistributionFixture.Create(
                signProductionDatabaseIdentity: true, signedBundleManifestSha256: new string('c', 64));
            using var handler = new FixtureHandler(fixture.NetworkAssets);
            var before = Snapshot(productRoot);

            var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CheckAsync(fixture, handler, productRoot));

            Assert.AreEqual("The installed catalog differs from its signed release identity.", exception.Message);
            CollectionAssert.AreEqual(before, Snapshot(productRoot));
        }
        finally
        {
            DeleteWritable(productRoot);
        }
    }

    private static Task<LifecycleResult> CheckAsync(CatalogDistributionFixture fixture, FixtureHandler handler, string productRoot)
        => CameraAgentLifecycleManager.ExecuteAsync(
            new LifecycleRequest(LifecycleOperationKind.CatalogCheck, null, productRoot, dryRun: false, resume: false, json: true)
            {
                AllowTestProductRoot = true,
                CatalogManifest = fixture.NetworkRequest(noDownload: false).CatalogManifest,
                Channel = DistributionChannel.Stable
            },
            new RefusingProcessRunner(), null, null, 0, 0, CancellationToken.None,
            () => new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot, catalogSpecifications: fixture.CatalogSpecifications));

    private static async Task<DistributionCatalogIdentity> ResolveSignedCatalogAsync(CatalogDistributionFixture fixture)
    {
        using var acquirer = new DistributionAcquirer(
            cacheRoot: fixture.CacheRoot, trustRoot: fixture.TrustRoot, catalogSpecifications: fixture.CatalogSpecifications);
        var resolved = await acquirer.ResolveCatalogAsync(fixture.LocalRequest(), CancellationToken.None);
        Assert.IsNotNull(resolved);
        return resolved.Catalog;
    }

    private static string RequireProductionBundle()
    {
        var bundle = Environment.GetEnvironmentVariable("HVO_PRODUCTION_CATALOG_BUNDLE");
        if (string.IsNullOrEmpty(bundle)) Assert.Inconclusive("Set HVO_PRODUCTION_CATALOG_BUNDLE to check an installed catalog.");
        return bundle;
    }

    // Installs the real HYG 4.2 bundle side by side and retains its identity exactly as catalog install records it.
    private static async Task<CatalogInstallationIdentity> InstallProductionPackageAsync(string bundle, string productRoot)
    {
        var paths = InstallationPaths.Create(productRoot, Guid.Empty, HygV42.CatalogId);
        var installed = CatalogInstaller.Install(bundle, paths.CatalogRoot, Guid.NewGuid());
        var references = Path.Combine(paths.CatalogReferencesRoot, installed.PackageVersion);
        Directory.CreateDirectory(references);
        await SafeFileSystem.WriteJsonAtomicAsync(
            Path.Combine(references, "installed.json"), installed,
            DeploymentJsonContext.Default.CatalogInstallationIdentity, CancellationToken.None);
        return installed;
    }

    private static void DeleteWritable(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root))
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(root, recursive: true);
    }

    // Retains an installed HYG 4.2 package side by side exactly as catalog install records it.
    private static CatalogInstallationIdentity InstallRetainedIdentity(string productRoot, string manifestSha256)
    {
        var paths = InstallationPaths.Create(productRoot, Guid.Empty, HygV42.CatalogId);
        var identity = new CatalogInstallationIdentity(
            HygV42.CatalogId, HygV42.PackageVersion, "2", "3", HygV42.DatabaseSha256, HygV42.DatabaseLength,
            HygV42.RowCount, paths.CatalogRoot, manifestSha256, "local-offline");
        Directory.CreateDirectory(Path.Combine(paths.CatalogRoot, "versions", HygV42.PackageVersion));
        var references = Path.Combine(paths.CatalogReferencesRoot, HygV42.PackageVersion);
        Directory.CreateDirectory(references);
        SafeFileSystem.WriteTextAtomic(
            Path.Combine(references, "installed.json"),
            JsonSerializer.Serialize(identity, DeploymentJsonContext.Default.CatalogInstallationIdentity));
        return identity;
    }

    private static string[] Snapshot(string root)
        => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => $"{Path.GetRelativePath(root, path)}:{(File.Exists(path) ? Hash(File.ReadAllBytes(path)) : "dir")}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static void AssertNoCacheEntries(CatalogDistributionFixture fixture)
        => Assert.IsFalse(
            Directory.Exists(fixture.CacheRoot) &&
            Directory.EnumerateFiles(fixture.CacheRoot, "*", SearchOption.AllDirectories).Any(),
            "A read-only catalog resolution wrote to the distribution cache.");

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class RefusingProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
            => throw new AssertFailedException($"A read-only catalog check started '{fileName}'.");
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
            Uri indexUri,
            IApprovedCatalogSpecificationSource catalogSpecifications)
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
            CatalogSpecifications = catalogSpecifications;
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

        // The synthetic bundle is approved only by this in-test specification; the reviewed production registry would
        // refuse its placeholder database identity before acquisition.
        public IApprovedCatalogSpecificationSource CatalogSpecifications { get; }

        /// <summary>
        /// Creates a signed catalog release. A read-only availability check never opens the bundle, so a caller may
        /// sign the pinned production database identity instead of the placeholder to model an installed package, and
        /// the installed bundle manifest digest so the signed identity names that exact package.
        /// </summary>
        public static CatalogDistributionFixture Create(
            bool signProductionDatabaseIdentity = false,
            string? signedBundleManifestSha256 = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"hvo-catalog-distribution-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var bundleFiles = Path.Combine(root, "bundle-files");
            Directory.CreateDirectory(bundleFiles);
            var databaseBytes = "sqlite-fixture"u8.ToArray();
            var (databaseSha256, databaseLength, rowCount) = signProductionDatabaseIdentity
                ? (HygV42.DatabaseSha256, HygV42.DatabaseLength, HygV42.RowCount)
                : (Hash(databaseBytes), databaseBytes.Length, 1L);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                manifestVersion = 2,
                package = new { kind = "production", version = "hyg-v4.2-p3-s2-r1" },
                catalog = new { id = "hyg-v42-production" },
                schemaVersion = "2",
                preprocessingVersion = "3",
                database = new { sha256 = databaseSha256, length = databaseLength, rowCount },
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
                signedBundleManifestSha256 ?? Hash(manifestBytes), databaseSha256, databaseLength, rowCount, "CC-BY-SA-4.0",
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
                root, trustRoot, manifestPath, bundlePath, assetName, assetSha256, network, bundleUri, indexUri,
                new ApprovedCatalogSpecificationSet(
                [
                    new ApprovedCatalogContract(
                        "hyg-v42-production", "hyg-v42-production-p3-s2", "hyg-v4.2-p3-s2-r", "HYG", "4.2", 2, "2", "3",
                        "hyg_v42.sqlite", databaseSha256, databaseLength, rowCount, "CC-BY-SA-4.0",
                        "LICENSE-HYG.md", "ATTRIBUTION-HYG.md", "fixture-topology", new string('e', 64))
                ]));
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
