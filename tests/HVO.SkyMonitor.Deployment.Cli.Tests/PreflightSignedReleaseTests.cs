using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Deployment.Distribution;
using Microsoft.Data.Sqlite;
using ContractReplayProfile = HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>The states a persisted CameraAgent database can be in when a preflight reads it.</summary>
public enum JournalShape
{
    /// <summary>Stopped and checkpointed: no wal-index and no log beside either database.</summary>
    Checkpointed,

    /// <summary>The raw-ingress journal is held by a writer, so its wal-index and log are both present.</summary>
    LiveRawIngressWal,

    /// <summary>A wal-index that outlived its log, which an in-place read-only open would recreate.</summary>
    WalIndexWithoutLog,

    /// <summary>
    /// The Identity database, which uses a rollback journal rather than WAL, has an uncommitted transaction open.
    /// An in-flight preflight runs before the drain, so this is the live shape it most often meets.
    /// </summary>
    HotIdentityRollbackJournal
}

/// <summary>
/// The operator-facing <c>cameraagent preflight</c> command reading its candidate from a signed image release.
/// The command answers whether the persisted state satisfies the boundaries that release declares, so it
/// resolves the release exactly as the upgrade does while remaining strictly read-only: nothing is pulled,
/// loaded, started, or written, including into the distribution download cache. The upgrade's contract-identity
/// gate is evaluated from the signed declaration; only its image-label-agreement gate runs during the upgrade.
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

    /// <summary>
    /// Every release-resolution option requires a release to resolve. `--channel local` names the default value,
    /// so presence rather than the resolved value has to decide, or it slips through unremarked.
    /// </summary>
    [TestMethod]
    [DataRow("--channel", "stable")]
    [DataRow("--channel", "local")]
    [DataRow("--asset-base-url", "https://mirror.example/releases")]
    public void CommandLine_PreflightReleaseOptionWithoutASignedRelease_IsRejected(string option, string value)
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(
        [
            "cameraagent", "preflight", "--instance-id", Guid.NewGuid().ToString("D"),
            "--product-root", "/tmp/hvo-preflight", option, value
        ]));

        StringAssert.Contains(exception.Message, "require --image-manifest or --image-index", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CommandLine_PreflightNoDownloadWithoutASignedRelease_IsRejected()
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(
        [
            "cameraagent", "preflight", "--instance-id", Guid.NewGuid().ToString("D"),
            "--product-root", "/tmp/hvo-preflight", "--no-download"
        ]));

        StringAssert.Contains(exception.Message, "require --image-manifest or --image-index", StringComparison.Ordinal);
    }

    /// <summary>An air-gapped operator forces cache-only resolution the same way an upgrade does.</summary>
    [TestMethod]
    public void CommandLine_PreflightNoDownload_IsCarriedIntoTheReleaseSelection()
    {
        var request = (CameraAgentStatePreflightRequest)CommandLine.ParseCommand(
        [
            "cameraagent", "preflight", "--instance-id", Guid.NewGuid().ToString("D"),
            "--product-root", "/tmp/hvo-preflight",
            "--image-manifest", "/media/hvo/image-v1.4.0/image-manifest.json", "--no-download"
        ]);

        Assert.IsTrue(request.NoDownload);
        Assert.IsTrue(request.ImageSelection().NoDownload);
    }

    [TestMethod]
    public void CommandLine_PreflightAssetBaseUrlThatIsNotAnHttpsLocator_IsRejected()
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(
        [
            "cameraagent", "preflight", "--instance-id", Guid.NewGuid().ToString("D"),
            "--product-root", "/tmp/hvo-preflight", "--channel", "stable",
            "--image-index", "https://mirror.example/indexes/image-stable-index.json",
            "--asset-base-url", "http://mirror.example/releases"
        ]));

        StringAssert.Contains(exception.Message, "--asset-base-url", StringComparison.Ordinal);
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

    /// <summary>
    /// The upgrade's contract-identity gate, reached from the signed declaration: a release declaring another
    /// configuration contract verifies cleanly (the verifier checks only the identity's shape) and used to preflight
    /// clean, then be refused after acquisition. It is now a blocking finding naming both contracts.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_ReleaseDeclaringAnotherConfigurationContract_IsReportedIncompatible()
    {
        using var instance = await InstalledInstanceFixture.CreateCurrentAsync();
        var drifted = new Dictionary<string, string>(SignedImageReleaseFixture.ContractLabels, StringComparer.Ordinal)
        {
            ["io.hvo.skymonitor.configuration-contract"] = "cameraagent-install-v2"
        };
        using var release = SignedImageReleaseFixture.Create(instance.Root, $"sha256:{new string('c', 64)}", drifted);
        var runner = new RefusingProcessRunner();

        var report = await CameraAgentStatePreflightManager.ExecuteAsync(
            instance.Request(release.ManifestPath), runner, CancellationToken.None, release.CreateAcquirer);

        Assert.IsFalse(report.Compatible);
        var finding = report.Findings.Single(static value => value.Code == "contract-configuration");
        Assert.IsTrue(finding.Blocking);
        Assert.AreEqual("contract-identity", finding.Boundary);
        Assert.AreEqual("io.hvo.skymonitor.configuration-contract", finding.Path);
        Assert.AreEqual("cameraagent-install-v2", finding.Observed);
        Assert.AreEqual("cameraagent-install-v1", finding.Expected);
        Assert.AreEqual(0, runner.Invocations, "the contract identities come from the signed record, never the image");
        StringAssert.Contains(
            CameraAgentStatePreflight.Render(report),
            "[blocking] contract-configuration (contract-identity) at io.hvo.skymonitor.configuration-contract: observed cameraagent-install-v2; expected cameraagent-install-v1.",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ExecuteAsync_ReleaseDeclaringAnotherCatalogContract_IsReportedIncompatible()
    {
        using var instance = await InstalledInstanceFixture.CreateCurrentAsync();
        var drifted = new Dictionary<string, string>(SignedImageReleaseFixture.ContractLabels, StringComparer.Ordinal)
        {
            ["io.hvo.skymonitor.catalog-contract"] = "hyg-v43-production-p3-s2"
        };
        using var release = SignedImageReleaseFixture.Create(instance.Root, $"sha256:{new string('c', 64)}", drifted);

        var report = await CameraAgentStatePreflightManager.ExecuteAsync(
            instance.Request(release.ManifestPath),
            new RefusingProcessRunner(),
            CancellationToken.None,
            release.CreateAcquirer);

        Assert.IsFalse(report.Compatible);
        var finding = report.Findings.Single(static value => value.Code == "contract-catalog");
        Assert.IsTrue(finding.Blocking);
        Assert.AreEqual("hyg-v43-production-p3-s2", finding.Observed);
        Assert.AreEqual("hyg-v42-production-p3-s2", finding.Expected);
        // The persisted-state boundaries are unaffected by an identity mismatch and still report clean.
        Assert.AreEqual(1, report.Findings.Count(static value => value.Blocking));
    }

    /// <summary>
    /// A LocalRunner instance dispatches archived replay to the local runner, so its candidate must declare the
    /// runner contract; a release built without it preflights clean today and is refused at upgrade time.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_LocalRunnerInstanceWithAReleaseOmittingTheReplayRunnerContract_IsReportedIncompatible()
    {
        using var instance = await InstalledInstanceFixture.CreateCurrentAsync(
            replayProfile: ContractReplayProfile.LocalRunner);
        var withoutRunner = new Dictionary<string, string>(SignedImageReleaseFixture.ContractLabels, StringComparer.Ordinal);
        withoutRunner.Remove("io.hvo.skymonitor.replay-runner-contract");
        using var release = SignedImageReleaseFixture.Create(instance.Root, $"sha256:{new string('c', 64)}", withoutRunner);

        var report = await CameraAgentStatePreflightManager.ExecuteAsync(
            instance.Request(release.ManifestPath),
            new RefusingProcessRunner(),
            CancellationToken.None,
            release.CreateAcquirer);

        Assert.IsFalse(report.Compatible, CameraAgentStatePreflight.Render(report));
        var finding = report.Findings.Single(static value => value.Code == "contract-replay-runner");
        Assert.IsTrue(finding.Blocking);
        Assert.AreEqual("io.hvo.skymonitor.replay-runner-contract", finding.Path);
        Assert.AreEqual("none", finding.Observed);
        Assert.AreEqual("local-replay-runner-v1", finding.Expected);
    }

    /// <summary>The same release is a valid candidate for an in-process instance, which imposes no runner requirement.</summary>
    [TestMethod]
    public async Task ExecuteAsync_InProcessInstanceWithAReleaseOmittingTheReplayRunnerContract_IsCompatible()
    {
        using var instance = await InstalledInstanceFixture.CreateCurrentAsync();
        var withoutRunner = new Dictionary<string, string>(SignedImageReleaseFixture.ContractLabels, StringComparer.Ordinal);
        withoutRunner.Remove("io.hvo.skymonitor.replay-runner-contract");
        using var release = SignedImageReleaseFixture.Create(instance.Root, $"sha256:{new string('c', 64)}", withoutRunner);

        var report = await CameraAgentStatePreflightManager.ExecuteAsync(
            instance.Request(release.ManifestPath),
            new RefusingProcessRunner(),
            CancellationToken.None,
            release.CreateAcquirer);

        Assert.IsTrue(report.Compatible, CameraAgentStatePreflight.Render(report));
        Assert.IsFalse(report.Findings.Any(static value => value.Boundary == "contract-identity"));
    }

    /// <summary>
    /// The component identity is compared too, for parity with the upgrade gate. Release verification already
    /// refuses a manifest whose image is not the CameraAgent component, so this mismatch cannot arrive through a
    /// verified release and is proved against the evaluation directly.
    /// </summary>
    [TestMethod]
    public async Task Evaluate_CandidateDeclaringAnotherComponent_IsReportedIncompatible()
    {
        using var instance = await InstalledInstanceFixture.CreateCurrentAsync();
        var requirements = new CameraAgentStateRequirements(
            CameraAgentStateContract.Current, new string('7', 40), CurrentIdentityMigration,
            CurrentRawIngressSchema, CurrentCatalogManifestVersion);

        var report = CameraAgentStatePreflight.Evaluate(
            instance.Paths,
            instance.InstanceId,
            $"sha256:{new string('c', 64)}",
            "image-v1.2.3",
            requirements,
            CameraAgentStateContract.LegacyUnbounded,
            RuntimeUid,
            RuntimeGid,
            ContractReplayProfile.InProcess,
            CameraAgentStateContractPolicy.RequireCurrent,
            new CameraAgentContractIdentity(
                "LogicHost", "cameraagent-install-v1", "hyg-v42-production-p3-s2", null),
            "cameraagent-install-v1");

        Assert.IsFalse(report.Compatible);
        var finding = report.Findings.Single(static value => value.Code == "contract-component");
        Assert.IsTrue(finding.Blocking);
        Assert.AreEqual("io.hvo.skymonitor.component", finding.Path);
        Assert.AreEqual("LogicHost", finding.Observed);
        Assert.AreEqual("CameraAgent", finding.Expected);
        Assert.AreEqual(1, report.Findings.Count(static value => value.Blocking));
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

        StringAssert.Contains(exception.Message, "does not support the instance's recorded Docker daemon's", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ExecuteAsync_SignedImageRelease_SelectsTheRecordedDaemonArchitectureNotTheProcess()
    {
        // The instance's recorded daemon architecture differs from the CLI process; the release publishes only the
        // daemon's architecture. The preflight must select exactly what the upgrade will acquire, without Docker.
        var daemonArchitecture = DistributionAcquirer.HostImageArchitecture() == "amd64" ? "arm64" : "amd64";
        using var instance = await InstalledInstanceFixture.CreateCurrentAsync(daemonArchitecture: daemonArchitecture);
        var candidateImageId = $"sha256:{new string('c', 64)}";
        using var release = SignedImageReleaseFixture.Create(
            instance.Root, candidateImageId, SignedImageReleaseFixture.ContractLabels, [daemonArchitecture]);
        var runner = new RefusingProcessRunner();

        var report = await CameraAgentStatePreflightManager.ExecuteAsync(
            instance.Request(release.ManifestPath), runner, CancellationToken.None, release.CreateAcquirer);

        Assert.IsTrue(report.Compatible, CameraAgentStatePreflight.Render(report));
        Assert.AreEqual(candidateImageId, report.CandidateImageId);
        Assert.AreEqual("image-v1.2.3", report.CandidateRelease);
        Assert.AreEqual(0, runner.Invocations, "preflight must not contact Docker to learn the daemon architecture");
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

    /// <summary>
    /// A rollback floor retained by an earlier acquisition still refuses an older index during a read-only
    /// resolution, and refusing it changes nothing on disk.
    /// </summary>
    [TestMethod]
    public async Task ResolveImageAsync_IndexBelowTheRetainedRollbackFloor_IsRefusedWithoutTouchingIt()
    {
        using var fixture = NetworkImageReleaseFixture.Create();
        var statePath = Path.Combine(fixture.CacheRoot, "test-state", "image-stable.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        await File.WriteAllTextAsync(statePath, $"9 {new string('e', 64)}\n");
        File.SetUnixFileMode(statePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var retained = await File.ReadAllBytesAsync(statePath);
        using var handler = new FixtureHandler(fixture.NetworkAssets);
        using var acquirer = new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => acquirer.ResolveImageAsync(NetworkImageReleaseFixture.IndexRequest(), CancellationToken.None));

        StringAssert.Contains(
            exception.Message, "older than or conflicts with retained rollback state", StringComparison.Ordinal);
        CollectionAssert.AreEqual(retained, await File.ReadAllBytesAsync(statePath));
    }

    /// <summary>
    /// A cached metadata entry that no longer verifies is bypassed, not evicted: an operator's preflight must not
    /// destroy the cache an acquisition is relying on. The same corruption drives an acquisition to replace it.
    /// </summary>
    [TestMethod]
    public async Task ResolveImageAsync_CachedMetadataThatNoLongerVerifies_IsBypassedAndLeftInPlace()
    {
        using var fixture = NetworkImageReleaseFixture.Create();
        using var warmingHandler = new FixtureHandler(fixture.NetworkAssets);
        using (var warming = new DistributionAcquirer(warmingHandler, fixture.CacheRoot, fixture.TrustRoot))
        {
            _ = await warming.AcquireImageAsync(
                NetworkImageReleaseFixture.ManifestRequest(), CancellationToken.None);
        }
        var cached = CachedManifestPath(fixture);
        var corrupt = await File.ReadAllBytesAsync(cached);
        corrupt[^1] ^= 1;
        await WriteCachedAsync(cached, corrupt);

        using var resolvingHandler = new FixtureHandler(fixture.NetworkAssets);
        using (var acquirer = new DistributionAcquirer(resolvingHandler, fixture.CacheRoot, fixture.TrustRoot))
        {
            var resolved = await acquirer.ResolveImageAsync(
                NetworkImageReleaseFixture.ManifestRequest(), CancellationToken.None);

            Assert.IsNotNull(resolved);
            Assert.AreEqual("image-v1.2.3", resolved.Release.Tag);
        }
        CollectionAssert.AreEqual(
            corrupt, await File.ReadAllBytesAsync(cached), "a read-only resolution must not evict a cache entry");

        using var acquiringHandler = new FixtureHandler(fixture.NetworkAssets);
        using var acquiring = new DistributionAcquirer(acquiringHandler, fixture.CacheRoot, fixture.TrustRoot);
        _ = await acquiring.AcquireImageAsync(NetworkImageReleaseFixture.ManifestRequest(), CancellationToken.None);

        CollectionAssert.AreNotEqual(
            corrupt,
            await File.ReadAllBytesAsync(CachedManifestPath(fixture)),
            "the contrast: an acquisition does replace the entry it could not verify");
    }

    /// <summary>
    /// The request's own option plumbing carries the index, the requested version, the asset base, and the
    /// channel: the index defaults to a superseded release, so a selector that fails to reach the resolver
    /// silently resolves the wrong manifest.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_IndexedReleaseSelectedThroughTheCommandLine_ResolvesTheRequestedVersion()
    {
        using var instance = await InstalledInstanceFixture.CreateCurrentAsync();
        using var fixture = NetworkImageReleaseFixture.Create();
        using var handler = new FixtureHandler(fixture.NetworkAssets);

        var report = await CameraAgentStatePreflightManager.ExecuteAsync(
            NetworkImageReleaseFixture.IndexPreflightRequest(instance.InstanceId, instance.Root),
            new RefusingProcessRunner(),
            CancellationToken.None,
            () => new DistributionAcquirer(handler, fixture.CacheRoot, fixture.TrustRoot));

        Assert.AreEqual("image-v1.2.3", report.CandidateRelease);
        Assert.IsTrue(report.Compatible, CameraAgentStatePreflight.Render(report));
        CollectionAssert.AreEqual(Array.Empty<string>(), Snapshot(fixture.CacheRoot));
    }

    /// <summary>
    /// A signed release can never present the superseded unbounded contract as an upgrade candidate: verification
    /// refuses the release outright, before the state boundaries are ever compared. This is why the signed path's
    /// <c>RequireCurrent</c> policy has no reachable candidate it would admit and <c>AllowLegacy</c> would not.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_ReleaseDeclaringTheSupersededContract_IsRefusedByVerificationItself()
    {
        using var instance = await InstalledInstanceFixture.CreateCurrentAsync();
        var superseded = new Dictionary<string, string>(SignedImageReleaseFixture.ContractLabels, StringComparer.Ordinal)
        {
            ["io.hvo.skymonitor.state-compatibility"] = CameraAgentStateContract.LegacyUnbounded
        };
        using var release = SignedImageReleaseFixture.Create(
            instance.Root, $"sha256:{new string('c', 64)}", superseded);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => CameraAgentStatePreflightManager.ExecuteAsync(
                instance.Request(release.ManifestPath),
                new RefusingProcessRunner(),
                CancellationToken.None,
                release.CreateAcquirer));

        StringAssert.Contains(
            exception.InnerException?.Message ?? exception.Message,
            "signed image compatibility identity is invalid",
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A journal carrying recovery state must not gain a file either. The wal-index legitimately changes content
    /// as readers register, so this pins the set of paths rather than their bytes; the checkpointed shape above
    /// pins the bytes.
    /// </summary>
    [TestMethod]
    [DataRow(JournalShape.LiveRawIngressWal)]
    [DataRow(JournalShape.WalIndexWithoutLog)]
    [DataRow(JournalShape.HotIdentityRollbackJournal)]
    public async Task ExecuteAsync_JournalCarryingRecoveryState_CreatesNoFileBesideTheDatabase(
        JournalShape shape)
    {
        using var instance = await InstalledInstanceFixture.CreateCurrentAsync(shape);
        using var release = SignedImageReleaseFixture.Create(
            instance.Root, $"sha256:{new string('c', 64)}", SignedImageReleaseFixture.ContractLabels);
        var before = SnapshotPaths(instance.Root);

        var report = await CameraAgentStatePreflightManager.ExecuteAsync(
            instance.Request(release.ManifestPath),
            new RefusingProcessRunner(),
            CancellationToken.None,
            release.CreateAcquirer);

        // Reading the shape correctly is half the guarantee: the boundaries this report compares are the ones
        // held in those databases, so an unreadable or rolled-forward read would not produce a compatible report.
        Assert.IsTrue(report.Compatible, CameraAgentStatePreflight.Render(report));
        CollectionAssert.AreEqual(before, SnapshotPaths(instance.Root), "preflight must create no file");
    }

    private static string[] SnapshotPaths(string root)
        => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string CachedManifestPath(NetworkImageReleaseFixture fixture)
        => Directory.EnumerateFiles(
                Path.Combine(fixture.CacheRoot, "v1", "metadata"), "*", SearchOption.AllDirectories)
            .Single(path => File.ReadAllBytes(path).Length == fixture.ManifestLength);

    private static async Task WriteCachedAsync(string path, byte[] bytes)
    {
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await File.WriteAllBytesAsync(path, bytes);
        File.SetUnixFileMode(path, UnixFileMode.UserRead);
    }

    /// <summary>
    /// Every path beneath a root with its mode and, for a file, a checksum of its content. Length alone would
    /// miss a same-length rewrite — a rewritten SQLite page, for instance — so the content itself is hashed.
    /// </summary>
    private static string[] Snapshot(string root)
        => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var relative = Path.GetRelativePath(root, path);
                if (File.Exists(path))
                {
                    return $"{relative}:{File.GetUnixFileMode(path)}:" +
                        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
                }
                // A dangling link resolves to no mode at all, so it is recorded by name.
                return Directory.Exists(path) ? $"{relative}/:{File.GetUnixFileMode(path)}" : $"{relative}?";
            })
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
        private SqliteConnection? writer;

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

        public static async Task<InstalledInstanceFixture> CreateCurrentAsync(
            JournalShape shape = JournalShape.Checkpointed,
            string? daemonArchitecture = null,
            ContractReplayProfile replayProfile = ContractReplayProfile.InProcess)
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
            fixture.ShapeJournals(shape);
            await fixture.WriteInstanceManifestAsync(daemonArchitecture, replayProfile).ConfigureAwait(false);
            return fixture;
        }

        /// <summary>
        /// Puts the persisted databases into one of the shapes a preflight can meet. Which one it is decides how
        /// each database may be read without writing beside it, so each is reproduced exactly rather than
        /// approximated, and the reproduction is asserted.
        /// </summary>
        private void ShapeJournals(JournalShape shape)
        {
            var database = CameraAgentStateLayout.RawIngressDatabasePath(Paths.StateRoot);
            var identity = CameraAgentStateLayout.IdentityDatabasePath(Paths.StateRoot);
            if (shape == JournalShape.LiveRawIngressWal)
            {
                // A running instance holds the journal open, so the wal-index and its log are both present.
                writer = new SqliteConnection($"Data Source={database}");
                writer.Open();
                using var command = writer.CreateCommand();
                command.CommandText = "INSERT INTO raw_capture (id) VALUES (1);";
                command.ExecuteNonQuery();
                AssertJournalFiles(database, "-wal", "-shm");
                return;
            }

            var walIndex = File.Exists(database + "-shm") ? File.ReadAllBytes(database + "-shm") : null;
            SqliteConnection.ClearAllPools();
            if (shape == JournalShape.WalIndexWithoutLog)
            {
                // A wal-index outliving its log: an in-place read-only open would recreate the log here.
                Assert.IsNotNull(walIndex, "the fixture must harvest a live wal-index, not fabricate one");
                File.WriteAllBytes(database + "-shm", walIndex);
                if (File.Exists(database + "-wal")) File.Delete(database + "-wal");
                AssertJournalFiles(database, "-shm");
                return;
            }
            if (shape == JournalShape.HotIdentityRollbackJournal)
            {
                // The Identity database is created through plain UseSqlite, so an open transaction leaves a
                // rollback journal and no wal-index. Only a private copy can be rolled back to read it.
                writer = new SqliteConnection($"Data Source={identity}");
                writer.Open();
                var transaction = writer.BeginTransaction();
                using var command = writer.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO __EFMigrationsHistory VALUES ('99999999999999_Uncommitted', '10.0.0');";
                command.ExecuteNonQuery();
                AssertJournalFiles(identity, "-journal");
                AssertJournalFiles(database);
                return;
            }
            // A stopped, checkpointed instance: the shape an operator preflights before an upgrade.
            AssertJournalFiles(database);
            AssertJournalFiles(identity);
        }

        private static void AssertJournalFiles(string database, params string[] expected)
        {
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                Assert.AreEqual(
                    expected.Contains(suffix, StringComparer.Ordinal),
                    File.Exists(database + suffix),
                    $"the fixture did not reproduce the requested journal shape at {suffix}");
            }
        }

        public void Dispose()
        {
            writer?.Dispose();
            SqliteConnection.ClearAllPools();
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
            // The runtime journal is WAL at rest, and a read-only SQLite handle creates the wal-index unless the
            // reader opts out, so the fixture must carry the production journal mode for the read-only guarantee
            // below to mean anything.
            command.CommandText =
                "PRAGMA journal_mode = WAL;" +
                "CREATE TABLE raw_capture (id INTEGER PRIMARY KEY);" +
                $"PRAGMA user_version = {CurrentRawIngressSchema.ToString(CultureInfo.InvariantCulture)};";
#pragma warning restore CA2100
            command.ExecuteNonQuery();
        }

        private Task WriteInstanceManifestAsync(
            string? daemonArchitecture = null,
            ContractReplayProfile replayProfile = ContractReplayProfile.InProcess)
        {
            var applicationIdentity = Guid.NewGuid();
            var catalog = new CatalogInstallationIdentity(
                ProductionCatalog.CatalogId, ProductionCatalog.PackageVersion, "2", "3",
                ProductionCatalog.DatabaseSha256, ProductionCatalog.DatabaseLength, ProductionCatalog.RowCount,
                Paths.CatalogRoot, new string('a', 64), "local-offline");
            // The recorded daemon architecture is what a signed-release preflight must select on; the installed
            // image's architecture agrees with it, as a real installation guarantees.
            var architecture = daemonArchitecture ?? DistributionAcquirer.HostImageArchitecture();
            var image = new ImageInstallationIdentity(
                "registry", $"cameraagent@sha256:{new string('b', 64)}", $"sha256:{new string('9', 64)}", architecture, null,
                UpgradeCompatibility: CameraAgentStateContract.LegacyUnbounded, SourceRevision: new string('8', 40),
                Component: "CameraAgent", ConfigurationContract: "cameraagent-install-v1",
                CatalogContract: "hyg-v42-production-p3-s2");
            var manifest = new InstanceManifest(
                1, "HVO.SkyMonitor", "cameraagent-install-v1", DeploymentComponent.CameraAgent, InstanceId,
                "Preflight Camera", applicationIdentity, $"installer-{InstanceId:D}", 1, new string('d', 64),
                "owner@example.test", 0, 0, 0, "UTC", Guid.NewGuid(), RuntimeUid, RuntimeGid, Paths.ProductRoot,
                Paths.ConfigRoot, Paths.StateRoot, ComposeDeployment.TemplateVersion, new string('e', 64),
                new string('f', 64), new string('1', 64), "default", "1", "1", "active", new string('2', 64),
                new string('3', 64), catalog, image, null,
                new DockerDaemonIdentity("daemon", "host", architecture, "29.7.2"),
                CameraAgentStateContract.LegacyUnbounded, DateTimeOffset.UtcNow,
                LifecycleCondition: InstanceLifecycleCondition.Installed,
                ReplayProfile: replayProfile);
            return SafeFileSystem.WriteJsonAtomicAsync(
                Paths.ManifestPath, manifest, DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        }
    }

    /// <summary>A signed CameraAgent image release published over HTTPS, both directly and through an index.</summary>
    private sealed class NetworkImageReleaseFixture : IDisposable
    {
        public const string AssetBase = "https://mirror.example/releases";

        private static readonly Uri ManifestUri =
            new("https://mirror.example/releases/image-v1.2.3/image-manifest.json");
        private static readonly Uri SupersededManifestUri =
            new("https://mirror.example/releases/image-v1.1.0/image-manifest.json");
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
        public int ManifestLength { get; private set; }
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
            AssetBaseUrl = AssetBase
        };

        /// <summary>
        /// The same index selection expressed as an operator would type it, so the request's own option plumbing
        /// carries the index, the version, the asset base, and the channel rather than a hand-built selection.
        /// </summary>
        public static CameraAgentStatePreflightRequest IndexPreflightRequest(Guid instanceId, string productRoot)
            => (CameraAgentStatePreflightRequest)CommandLine.ParseCommand(
            [
                "cameraagent", "preflight", "--instance-id", instanceId.ToString("D"),
                "--product-root", productRoot, "--channel", "stable",
                "--image-index", IndexUri.AbsoluteUri, "--image-version", "1.2.3",
                "--asset-base-url", AssetBase
            ]);

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

            // A superseded release is published alongside, and it is the index default, so an --image-version
            // that fails to reach the resolver silently selects the wrong manifest instead of passing anyway.
            var superseded = manifest with
            {
                Release = manifest.Release with { Version = "1.1.0", Tag = "image-v1.1.0" }
            };
            var supersededBytes = JsonSerializer.SerializeToUtf8Bytes(
                superseded, DistributionJsonContext.Default.DistributionReleaseManifest);
            network[SupersededManifestUri] = supersededBytes;
            network[new Uri(SupersededManifestUri.AbsoluteUri + ".sig")] = Sign(key, supersededBytes);

            var index = new DistributionReleaseIndex(
                DistributionSchemaVersions.ReleaseIndex,
                "release-index",
                "image",
                1,
                DateTimeOffset.Parse("2026-08-24T00:00:00Z", CultureInfo.InvariantCulture),
                "1.1.0",
                new DistributionSigningIdentity(DistributionTrustRoot.Algorithm, trustRoot.KeyId),
                [
                    new DistributionReleaseReference(
                        "1.2.3",
                        "image-v1.2.3",
                        "image-manifest.json",
                        manifestBytes.Length,
                        Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
                        "image-manifest.json.sig"),
                    new DistributionReleaseReference(
                        "1.1.0",
                        "image-v1.1.0",
                        "image-manifest.json",
                        supersededBytes.Length,
                        Convert.ToHexStringLower(SHA256.HashData(supersededBytes)),
                        "image-manifest.json.sig")
                ]);
            var indexBytes = JsonSerializer.SerializeToUtf8Bytes(
                index, DistributionJsonContext.Default.DistributionReleaseIndex);
            network[IndexUri] = indexBytes;
            network[new Uri(IndexUri.AbsoluteUri + ".sig")] = Sign(key, indexBytes);
            return new NetworkImageReleaseFixture(root, trustRoot, imageId, network)
            {
                ManifestLength = manifestBytes.Length
            };
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static byte[] Sign(ECDsa key, byte[] bytes)
            => Encoding.ASCII.GetBytes(Convert.ToBase64String(key.SignData(
                bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) + "\n");
    }
}
