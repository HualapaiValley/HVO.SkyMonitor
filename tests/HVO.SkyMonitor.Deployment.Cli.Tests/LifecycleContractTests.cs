using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task StatusAsync_ReportsRetainedIdentityWithoutMutation()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var request = fixture.Request(operation: null);

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("status", result.Outcome);
        Assert.AreEqual(fixture.InstanceId, result.InstanceId);
        Assert.AreEqual(fixture.Manifest.Image, result.Image);
        Assert.IsFalse(result.Running);
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_ReleaseWithoutThisHostArchitecture_FailsBeforeAnyMutation()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var other = ReleaseAcquirer.HostImageArchitecture() == "amd64" ? "arm64" : "amd64";
        using var release = ReleaseFixture.Create(fixture.Root, platforms: [$"linux/{other}"]);
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            Release = release.Folder,
            MigrationBackwardCompatible = true
        };

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            CameraAgentLifecycleManager.ExecuteAsync(
                request, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        // The upgrade selects for the instance's recorded daemon, which this fixture records as the process
        // architecture by default (on amd64 and arm64 runners alike), so the release that publishes only the other
        // architecture is refused and the refusal names the daemon as the selecting authority.
        StringAssert.Contains(exception.Message, "does not support the instance's recorded Docker daemon's", StringComparison.Ordinal);
        // The release is read before the instance is touched, so no lifecycle operation is journaled and Docker is
        // never asked anything.
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Paths.DeploymentStateRoot, "image-distribution.json")));
        Assert.AreEqual(0, fixture.Runner.InvocationCount);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_ReleaseSelectsTheRecordedDaemonArchitectureNotTheProcess()
    {
        // The instance was installed against a daemon whose architecture differs from the CLI process (a remote or
        // cross-architecture DOCKER_HOST). The release publishes only that architecture.
        var daemonArchitecture = ReleaseAcquirer.HostImageArchitecture() == "amd64" ? "arm64" : "amd64";
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner,
            daemonArchitecture: daemonArchitecture);
        using var release = CandidateRelease(fixture, out var candidateImageId, platforms: [$"linux/{daemonArchitecture}"]);
        fixture.Runner.CandidateRawIngressSchema = "13";

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                Release = release.Folder,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(candidateImageId, result.Image!.ImageId);
        Assert.AreEqual(daemonArchitecture, result.Image.Architecture);
        Assert.AreEqual("archive", result.Image.Source);
        Assert.AreEqual((string)release.Archive($"linux/{daemonArchitecture}")["sha256"]!, result.Image.ArchiveSha256);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_ReleaseWithoutTheRecordedDaemonArchitecture_FailsBeforeAnyLoad()
    {
        // The release publishes only the CLI process's architecture, which the recorded daemon cannot run: the
        // refusal names the published platforms and the daemon, and nothing is loaded or journalled.
        var processArchitecture = ReleaseAcquirer.HostImageArchitecture();
        var daemonArchitecture = processArchitecture == "amd64" ? "arm64" : "amd64";
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed, daemonArchitecture: daemonArchitecture);
        using var release = ReleaseFixture.Create(fixture.Root, platforms: [$"linux/{processArchitecture}"]);
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            Release = release.Folder,
            MigrationBackwardCompatible = true
        };

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            CameraAgentLifecycleManager.ExecuteAsync(
                request, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.AreEqual(
            $"Release v{release.Version} publishes the CameraAgent image for linux/{processArchitecture} and does not support " +
            $"the instance's recorded Docker daemon's linux/{daemonArchitecture} architecture.",
            exception.Message);
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
        Assert.AreEqual(0, fixture.Runner.InvocationCount, "nothing may be loaded for a platform the recorded daemon cannot run");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_ReleaseThatFailsVerification_IsRefusedBeforeTheJournalOrDocker()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        using var release = CandidateRelease(fixture, out _);
        // release.json changed after SHA256SUMS was written: it is not the release.json the release published.
        await File.AppendAllTextAsync(Path.Combine(release.Folder, ReleaseManifest.FileName), " ");

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            CameraAgentLifecycleManager.ExecuteAsync(
                fixture.Request(LifecycleOperationKind.Upgrade) with { Release = release.Folder },
                fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.StartsWith(exception.Message, "release.json does not match its SHA-256 in SHA256SUMS", StringComparison.Ordinal);
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
        Assert.AreEqual(0, fixture.Runner.InvocationCount);
    }

    /// <summary>
    /// A release whose offline archive holds an image that is not the release's, or not one this instance can run, is
    /// refused from the archive before the journal entry and before Docker is asked to load anything.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("version-label", "holds an image that is not labelled as CameraAgent 1.2.0-dev.3 from commit 0123456789abcdef0123456789abcdef01234567, created 2026-10-17T18:00:00Z, as release v1.2.0-dev.3 requires.")]
    [DataRow("created-label", "holds an image that is not labelled as CameraAgent 1.2.0-dev.3 from commit 0123456789abcdef0123456789abcdef01234567, created 2026-10-17T18:00:00Z, as release v1.2.0-dev.3 requires.")]
    [DataRow("contract-label", "!The candidate image does not declare the required CameraAgent configuration and catalog contracts.")]
    [DataRow("state-contract", "!The candidate image declares state compatibility")]
    public async Task UpgradeAsync_ReleaseArchiveNotHoldingTheReleasesImage_IsRefusedBeforeTheJournalOrALoad(string fault, string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        using var release = CandidateRelease(fixture, out _, labels: labels =>
        {
            switch (fault)
            {
                case "version-label": labels["org.opencontainers.image.version"] = "9.9.9"; break;
                case "created-label": labels.Remove("org.opencontainers.image.created"); break;
                case "contract-label": labels.Remove("io.hvo.skymonitor.configuration-contract"); break;
                case "state-contract": labels["io.hvo.skymonitor.state-compatibility"] = "cameraagent-state-v1"; break;
            }
        });
        fixture.Runner.CandidateHeld = [];
        var instanceBefore = InstanceTree(fixture);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with { Release = release.Folder, MigrationBackwardCompatible = true },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        var platform = $"linux/{fixture.Manifest.DockerDaemon.Architecture}";
        if (expected.StartsWith('!'))
            StringAssert.StartsWith(exception.Message, expected[1..], StringComparison.Ordinal);
        else
            Assert.AreEqual($"The offline archive {(string)release.Archive(platform)["name"]!} {expected}", exception.Message);
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath), "a refused release must not open the journal");
        Assert.AreEqual(0, fixture.Runner.LoadCount, "a refused archive must never be loaded");
        Assert.IsEmpty(fixture.Runner.CandidateHeld!);
        CollectionAssert.AreEqual(instanceBefore, InstanceTree(fixture), "a refused release changed the instance");
    }

    /// <summary>
    /// A registry release image is read from its registry by the digest release.json names, and refused there, before
    /// anything is pulled and before the journal entry, so the instance, its journal and Docker end as they began.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("version-label", "The CameraAgent image {reference} in its registry is not labelled as CameraAgent 1.2.0-dev.3 from commit 0123456789abcdef0123456789abcdef01234567, created 2026-10-17T18:00:00Z, as release v1.2.0-dev.3 requires. Nothing was pulled.")]
    [DataRow("created-label", "The CameraAgent image {reference} in its registry is not labelled as CameraAgent 1.2.0-dev.3 from commit 0123456789abcdef0123456789abcdef01234567, created 2026-10-17T18:00:00Z, as release v1.2.0-dev.3 requires. Nothing was pulled.")]
    [DataRow("contract-label", "The candidate image does not declare the required CameraAgent configuration and catalog contracts.")]
    [DataRow("state-contract", "!The candidate image declares state compatibility")]
    [DataRow("unavailable", "The installer cannot read {reference} from its registry before pulling it, so it pulls nothing (docker: 'buildx' is not a docker command.). Install from the release's offline archive, the default image source (leave out --image-source registry), or let this user read the registry with 'docker buildx imagetools inspect' (buildx 0.8 or later).")]
    public async Task UpgradeAsync_RegistryReleaseImageThatIsRefused_IsRefusedBeforeAnyPullOrTheJournal(string fault, string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        using var release = CandidateRelease(fixture, out var candidateImageId, labels: labels =>
        {
            switch (fault)
            {
                case "version-label": labels["org.opencontainers.image.version"] = "9.9.9"; break;
                case "created-label": labels.Remove("org.opencontainers.image.created"); break;
                case "contract-label": labels.Remove("io.hvo.skymonitor.configuration-contract"); break;
                case "state-contract": labels["io.hvo.skymonitor.state-compatibility"] = "cameraagent-state-v1"; break;
            }
        });
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            release.DigestReference, candidateImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.Registry = new FakeRegistry(release.RegistryContent)
        {
            Unavailable = fault == "unavailable" ? "docker: 'buildx' is not a docker command." : null
        };
        fixture.Runner.CandidateHeld = [];
        var instanceBefore = InstanceTree(fixture);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                Release = release.Folder,
                ImageSource = ReleaseImageSource.Registry,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        var message = expected.Replace("{reference}", release.DigestReference, StringComparison.Ordinal);
        if (message.StartsWith('!'))
            StringAssert.StartsWith(exception.Message, message[1..], StringComparison.Ordinal);
        else
            Assert.AreEqual(message, exception.Message);
        Assert.IsGreaterThan(0, fixture.Runner.Registry!.Reads);
        Assert.AreEqual(0, fixture.Runner.PullCount, "a refused registry image must never be pulled");
        Assert.IsEmpty(fixture.Runner.RemovedImages, "nothing was pulled, so nothing is removed");
        Assert.IsEmpty(fixture.Runner.CandidateHeld!);
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath), "a refused release must not open the journal");
        CollectionAssert.AreEqual(instanceBefore, InstanceTree(fixture), "a refused release changed the instance");
    }

    /// <summary>
    /// The checks on the pulled image stay as the second line: a registry whose metadata is the release's, but whose
    /// pulled image Docker then reports otherwise, has that image removed again before the journal entry, so the
    /// instance, its journal and Docker end as they began.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("version-label", "The CameraAgent image Docker resolved is not labelled as CameraAgent 1.2.0-dev.3 from commit 0123456789abcdef0123456789abcdef01234567, created 2026-10-17T18:00:00Z, as release v1.2.0-dev.3 requires.")]
    [DataRow("created-label", "The CameraAgent image Docker resolved is not labelled as CameraAgent 1.2.0-dev.3 from commit 0123456789abcdef0123456789abcdef01234567, created 2026-10-17T18:00:00Z, as release v1.2.0-dev.3 requires.")]
    [DataRow("contract-label", "The candidate image does not declare the required CameraAgent configuration and catalog contracts.")]
    public async Task UpgradeAsync_PulledImageDockerReportsAsAnotherRelease_IsRemovedBeforeTheJournal(string fault, string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        using var release = CandidateRelease(fixture, out var candidateImageId);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            release.DigestReference, candidateImageId, fixture.Uid, fixture.Gid);
        // The registry's metadata is the release's, but Docker reports the image it pulled otherwise.
        fixture.Runner.Registry = new FakeRegistry(release.RegistryContent);
        switch (fault)
        {
            case "version-label": fixture.Runner.CandidateLabels["org.opencontainers.image.version"] = "9.9.9"; break;
            case "created-label": fixture.Runner.CandidateLabels["org.opencontainers.image.created"] = null; break;
            case "contract-label": fixture.Runner.CandidateLabels["io.hvo.skymonitor.configuration-contract"] = null; break;
        }
        fixture.Runner.CandidateHeld = [];
        var instanceBefore = InstanceTree(fixture);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                Release = release.Folder,
                ImageSource = ReleaseImageSource.Registry,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.AreEqual(expected, exception.Message);
        Assert.AreEqual(1, fixture.Runner.PullCount);
        CollectionAssert.Contains(fixture.Runner.RemovedImages, release.DigestReference);
        Assert.IsEmpty(fixture.Runner.CandidateHeld!, "the refused pull is still in Docker");
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath), "a refused release must not open the journal");
        CollectionAssert.AreEqual(instanceBefore, InstanceTree(fixture), "a refused release changed the instance");
    }

    /// <summary>
    /// An upgrade cancelled while Docker pulls the release's image, after the image arrived, removes it again before the
    /// journal entry, so the instance, its journal and Docker end as they began.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_CancelledWhileTheReleaseImageIsPulled_RemovesItBeforeTheJournal()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        using var release = CandidateRelease(fixture, out var candidateImageId);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            release.DigestReference, candidateImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.Registry = new FakeRegistry(release.RegistryContent);
        using var cancellation = new CancellationTokenSource();
        fixture.Runner.CancelDuringPull = cancellation;
        fixture.Runner.CandidateHeld = [];
        var instanceBefore = InstanceTree(fixture);

        await Assert.ThrowsAsync<OperationCanceledException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                Release = release.Folder,
                ImageSource = ReleaseImageSource.Registry,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, cancellation.Token));

        Assert.AreEqual(1, fixture.Runner.PullCount);
        CollectionAssert.Contains(fixture.Runner.RemovedImages, release.DigestReference);
        Assert.IsEmpty(fixture.Runner.CandidateHeld!, "the cancelled pull is still in Docker");
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath), "a cancelled pull must not open the journal");
        CollectionAssert.AreEqual(instanceBefore, InstanceTree(fixture), "a cancelled pull changed the instance");
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
    }

    /// <summary>
    /// Every path under the instance root with its length, relative and sorted, for before-and-after comparison. Lock
    /// files coordinate operations and hold no state, so they are left out.
    /// </summary>
    private static string[] InstanceTree(LifecycleFixture fixture)
        => [.. Directory.EnumerateFileSystemEntries(fixture.Paths.InstanceRoot, "*", SearchOption.AllDirectories)
            .Where(static path => !path.EndsWith(".lock", StringComparison.Ordinal))
            .Select(path => $"{Path.GetRelativePath(fixture.Paths.InstanceRoot, path)} {(File.Exists(path) ? new FileInfo(path).Length : -1)}")
            .Order(StringComparer.Ordinal)];

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_ReleaseOlderThanTheRecordedRelease_IsRefusedBeforeTheJournal()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        using var release = CandidateRelease(fixture, out _);
        await RecordInstalledReleaseAsync(fixture, "1.3.0");

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            CameraAgentLifecycleManager.ExecuteAsync(
                fixture.Request(LifecycleOperationKind.Upgrade) with { Release = release.Folder, MigrationBackwardCompatible = true },
                fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
                fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.StartsWith(
            exception.Message, $"1.3.0 is installed, which is newer than the upgrade target {release.Version}; an upgrade never goes back.",
            StringComparison.Ordinal);
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_Release_ShowsItsUpgradeNotesAndRecordsItsVersionForTheNextUpgrade()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        using var release = CandidateRelease(fixture, out var candidateImageId, notes: [("1.1.0", "Eleven."), ("1.2.0", "Twelve.")]);
        fixture.Runner.CandidateRawIngressSchema = "13";
        await RecordInstalledReleaseAsync(fixture, "1.1.0");
        using var output = new StringWriter();

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with { Release = release.Folder, MigrationBackwardCompatible = true },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, output: output, error: TextWriter.Null);

        Assert.AreEqual("completed", result.Outcome);
        // Only the notes after the installed 1.1.0, up to the release 1.2.0-dev.3 belongs to.
        Assert.AreEqual("1.1.0", result.UpgradeNotes!.InstalledVersion);
        Assert.AreEqual(release.Version, result.UpgradeNotes.TargetVersion);
        Assert.AreEqual("1.2.0", UpgradeNotesTests.Versions(result.UpgradeNotes.Notes));
        StringAssert.Contains(output.ToString(), $"Upgrading from 1.1.0 to {release.Version}.", StringComparison.Ordinal);
        Assert.AreEqual(
            release.Version,
            await UpgradeNotes.InstalledVersionAsync(fixture.Paths, result.Image!, CancellationToken.None));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_ReleaseFromTheRegistry_PullsItsDigestReference()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        using var release = CandidateRelease(fixture, out var candidateImageId);
        // A pulled image is inspected by the digest reference it was pulled by.
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            release.DigestReference, candidateImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.CandidateRawIngressSchema = "13";
        fixture.Runner.Registry = new FakeRegistry(release.RegistryContent);

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                Release = release.Folder,
                ImageSource = ReleaseImageSource.Registry,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(candidateImageId, result.Image!.ImageId);
        Assert.AreEqual(release.DigestReference, result.Image.ImmutableReference);
        Assert.AreEqual("registry", result.Image.Source);
        // The index, the platform manifest and the configuration were read before anything was pulled.
        Assert.AreEqual(3, fixture.Runner.Registry!.Reads);
        Assert.IsGreaterThan(0, fixture.Runner.PullCount);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UninstallAsync_InstalledInstanceDrainsAndPreservesState()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid);
        var stateSentinel = Path.Combine(fixture.Paths.StateRoot, "sentinel");
        await File.WriteAllTextAsync(stateSentinel, "preserved");
        var lifecycle = new FakeLifecycleClient();

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Uninstall), fixture.Runner, _ => lifecycle, null,
            fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(InstanceLifecycleCondition.Uninstalled, result.LifecycleCondition);
        Assert.IsTrue(File.Exists(stateSentinel));
        Assert.IsNull(fixture.Runner.ActiveImageId);
        Assert.AreEqual(1, lifecycle.PauseCount);
        Assert.AreEqual(1, fixture.Runner.ComposeDownCount);
        var operation = await CameraAgentLifecycleManager.ReadOperationAsync(
            fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationPhase.Completed, operation!.Phase);
        Assert.IsFalse(operation.MutationStarted);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UninstallAsync_OwnershipDriftIsRejectedBeforeComposeDown()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.OmitOwnershipLabel = true;

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Uninstall), fixture.Runner, _ => new FakeLifecycleClient(), null,
            fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(0, fixture.Runner.ComposeDownCount);
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
        var retained = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual(InstanceLifecycleCondition.Installed, retained.LifecycleCondition);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UninstallAsync_OwnedOrphanIsRejectedBeforeManifestCommit()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.RetainOwnedOrphanAfterDown = true;

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Uninstall), fixture.Runner, _ => new FakeLifecycleClient(), null,
            fixture.Uid, fixture.Gid, CancellationToken.None));

        var retained = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual(InstanceLifecycleCondition.Installed, retained.LifecycleCondition);
        Assert.AreEqual(1, fixture.Runner.ComposeDownCount);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PurgeAsync_InstalledInstance_IsRejectedBeforeDeletion()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var request = fixture.Request(LifecycleOperationKind.Purge) with
        {
            ConfirmationInstanceId = fixture.InstanceId
        };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.IsTrue(Directory.Exists(fixture.Paths.InstanceRoot));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PurgeAsync_UninstalledInstanceDeletesOnlySelectedSibling()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Uninstalled);
        var sibling = Path.Combine(fixture.Paths.ProductRoot, "cameraagents", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(sibling);
        File.SetUnixFileMode(sibling, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(Path.Combine(sibling, "sentinel"), "preserved");
        var request = fixture.Request(LifecycleOperationKind.Purge) with { ConfirmationInstanceId = fixture.InstanceId };

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.IsFalse(Directory.Exists(fixture.Paths.InstanceRoot));
        Assert.IsTrue(File.Exists(Path.Combine(sibling, "sentinel")));
        Assert.AreEqual(0, fixture.Runner.SudoCount);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PurgeAsync_InterruptedTombstoneResumesDeletionWithoutRepublishing()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Uninstalled, catalogId: "hyg-v44-production");
        var request = fixture.Request(LifecycleOperationKind.Purge) with { ConfirmationInstanceId = fixture.InstanceId };
        var operation = await CameraAgentLifecycleManager.BeginAsync(
            request, fixture.Paths, LifecycleOperationKind.Purge, fixture.Manifest, CancellationToken.None);
        var parent = Path.GetDirectoryName(fixture.Paths.InstanceRoot)!;
        var childName = Path.GetFileName(fixture.Paths.InstanceRoot);
        var inventory = SafeTreeDeletion.CaptureChildInventory(parent, childName, fixture.Uid, fixture.Gid);
        await SafeFileSystem.WriteJsonAtomicAsync(
            Path.Combine(fixture.Paths.OperationsRoot, $"purge-{operation.OperationId:D}.evidence.json"),
            new PurgeDeletionEvidence(
                DeploymentSchemaVersions.LifecycleOperation, operation.OperationId, operation.RequestSha256,
                LocalHostIdentity.ReadSha256(), fixture.Manifest, inventory),
            DeploymentJsonContext.Default.PurgeDeletionEvidence, CancellationToken.None);
        operation = await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with
        {
            MutationStarted = true,
            Phase = LifecycleOperationPhase.Mutating
        }, CancellationToken.None);
        var identity = SafeTreeDeletion.ValidateChild(parent, childName, fixture.Uid, fixture.Gid);
        var tombstoneName = $".purge-{fixture.InstanceId:D}-{operation.OperationId:D}";
        SafeTreeDeletion.RenameChild(parent, childName, tombstoneName, identity);
        File.Delete(Path.Combine(parent, tombstoneName, "instance-manifest.json"));

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            request with { Resume = true }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.IsFalse(Directory.Exists(fixture.Paths.InstanceRoot));
        Assert.IsFalse(Directory.EnumerateDirectories(parent, ".purge-*", SearchOption.TopDirectoryOnly).Any());
        Assert.Contains(fixture.Paths.CatalogRoot, result.PreservedPaths);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void OperationLock_AcquisitionHonorsCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-lifecycle-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "operation.lock");
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.ThrowsExactly<OperationCanceledException>(() => OperationLock.Acquire(
                path, TimeSpan.FromSeconds(5), cancellation.Token));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task SafeTreeDeletion_RemovesOnlyAuthenticatedChild()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"hvo-safe-delete-{Guid.NewGuid():N}");
        var child = Path.Combine(parent, "child");
        Directory.CreateDirectory(Path.Combine(child, "nested"));
        File.SetUnixFileMode(child, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(Path.Combine(child, "nested"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(Path.Combine(child, "nested", "value"), "value");
        try
        {
            SafeTreeDeletion.DeleteChild(parent, "child", NativeLinux.getuid(), NativeLinux.getgid());

            Assert.IsFalse(Directory.Exists(child));
            Assert.IsTrue(Directory.Exists(parent));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void SafeTreeDeletion_RejectsSymbolicLinkEntry()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"hvo-safe-delete-{Guid.NewGuid():N}");
        var child = Path.Combine(parent, "child");
        Directory.CreateDirectory(child);
        File.CreateSymbolicLink(Path.Combine(child, "escape"), "/etc/passwd");
        try
        {
            Assert.ThrowsExactly<InstallerException>(() =>
                SafeTreeDeletion.DeleteChild(parent, "child", NativeLinux.getuid(), NativeLinux.getgid()));
            Assert.IsTrue(Directory.Exists(child));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void SafeTreeDeletion_RenameAndDeleteRemainBoundToValidatedIdentity()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"hvo-safe-rename-{Guid.NewGuid():N}");
        var child = Path.Combine(parent, "child");
        Directory.CreateDirectory(child);
        File.SetUnixFileMode(child, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var identity = SafeTreeDeletion.ValidateChild(parent, "child", NativeLinux.getuid(), NativeLinux.getgid());
            SafeTreeDeletion.RenameChild(parent, "child", "tombstone", identity);
            SafeTreeDeletion.DeleteChild(parent, "tombstone", NativeLinux.getuid(), NativeLinux.getgid(), identity);

            Assert.IsFalse(Directory.Exists(Path.Combine(parent, "tombstone")));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void SafeTreeDeletion_RejectsGroupWritableDirectory()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"hvo-safe-mode-{Guid.NewGuid():N}");
        var child = Path.Combine(parent, "child");
        Directory.CreateDirectory(child);
        File.SetUnixFileMode(child,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite);
        try
        {
            Assert.ThrowsExactly<InstallerException>(() =>
                SafeTreeDeletion.ValidateChild(parent, "child", NativeLinux.getuid(), NativeLinux.getgid()));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task LifecycleControlToken_MissingOrMismatchedMirrorIsRejectedWithoutMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-lifecycle-token-{Guid.NewGuid():N}");
        var paths = InstallationPaths.Create(root, Guid.NewGuid(), HygV42.CatalogId);
        Directory.CreateDirectory(Path.Combine(paths.ConfigRoot, "lifecycle-control"));
        Directory.CreateDirectory(Path.Combine(paths.ConfigRoot, "secrets"));
        var tokenPath = Path.Combine(paths.ConfigRoot, "lifecycle-control", "token");
        var mirrorPath = Path.Combine(paths.ConfigRoot, "secrets", "LifecycleControl__Token");
        SafeFileSystem.WriteTextAtomic(tokenPath, "authority-token");
        try
        {
            await Assert.ThrowsExactlyAsync<InstallerException>(() =>
                CameraAgentLifecycleManager.ReadLifecycleControlTokenAsync(paths, CancellationToken.None));
            Assert.IsFalse(File.Exists(mirrorPath));

            SafeFileSystem.WriteTextAtomic(mirrorPath, "authority-token");
            var token = await CameraAgentLifecycleManager.ReadLifecycleControlTokenAsync(paths, CancellationToken.None);
            Assert.AreEqual("authority-token", token);

            SafeFileSystem.WriteTextAtomic(mirrorPath, "different-token");
            await Assert.ThrowsExactlyAsync<InstallerException>(() =>
                CameraAgentLifecycleManager.ReadLifecycleControlTokenAsync(paths, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    [DataRow("lifecycle-mirror")]
    [DataRow("catalog-selection")]
    [DataRow("catalog-id")]
    [DataRow("installation-verification")]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task LifecycleOperation_IncompleteCanonicalSecretsFailBeforeDockerOrJournalMutation(string missingSecret)
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            missingSecret == "installation-verification"
                ? InstanceLifecycleCondition.Uninstalled
                : InstanceLifecycleCondition.Installed);
        var path = missingSecret switch
        {
            "lifecycle-mirror" => Path.Combine(fixture.Paths.ConfigRoot, "secrets", "LifecycleControl__Token"),
            "catalog-selection" => Path.Combine(fixture.Paths.ConfigRoot, "secrets", "Catalog__RequiredPackageVersion"),
            "catalog-id" => Path.Combine(fixture.Paths.ConfigRoot, "secrets", "Catalog__RequiredCatalogId"),
            _ => Path.Combine(fixture.Paths.ConfigRoot, "installation-verification", "token")
        };
        File.Delete(path);
        var operation = missingSecret == "installation-verification"
            ? LifecycleOperationKind.Reinstall
            : LifecycleOperationKind.Uninstall;

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(operation), fixture.Runner, null, null,
            fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(exception.Message, "credential", StringComparison.Ordinal);
        Assert.IsFalse(File.Exists(path));
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
        Assert.AreEqual(0, fixture.Runner.InvocationCount);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ReadOperationAsync_InvalidJournalIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-invalid-operation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "operation.json");
        SafeFileSystem.WriteTextAtomic(path, "{}");
        try
        {
            await Assert.ThrowsExactlyAsync<InstallerException>(() =>
                CameraAgentLifecycleManager.ReadOperationAsync(path, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void CatalogGarbageCollect_MutationRequiresExplicitVersion()
    {
        var request = new LifecycleRequest(
            LifecycleOperationKind.CatalogGarbageCollect,
            instanceId: null,
            InstallRequest.DefaultProductRoot,
            dryRun: false,
            resume: false,
            json: false);

        Assert.ThrowsExactly<InstallUsageException>(request.Validate);
    }

    [TestMethod]
    public void LifecycleRequest_PasswordFreeReceiptHashRemainsStable()
    {
        var request = new LifecycleRequest(
            LifecycleOperationKind.Uninstall,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            InstallRequest.DefaultProductRoot,
            dryRun: false,
            resume: false,
            json: false);

        Assert.AreEqual("1333fe883fe6083f51c80f612b6ae0164741f300e7a4c72270c269578cc3d7f3", request.ComputeRequestSha256());
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CatalogGarbageCollect_InterruptedDeletionRequiresExactResumeRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-catalog-gc-{Guid.NewGuid():N}");
        var paths = InstallationPaths.Create(root, Guid.Empty, HygV42.CatalogId);
        var versionsRoot = Path.Combine(paths.CatalogRoot, "versions");
        var version = "hyg-v4.2-p3-s2-r2";
        var operationId = Guid.NewGuid();
        var tombstoneName = $".gc-{version}-{operationId:N}";
        var tombstone = Path.Combine(versionsRoot, tombstoneName);
        Directory.CreateDirectory(tombstone);
        Directory.CreateDirectory(paths.OperationsRoot);
        Directory.CreateDirectory(Path.Combine(versionsRoot, HygV42.PackageVersion));
        Directory.CreateSymbolicLink(Path.Combine(paths.CatalogRoot, "current"), $"versions/{HygV42.PackageVersion}");
        await File.WriteAllTextAsync(Path.Combine(tombstone, "remaining"), "remaining");
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root))
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var uid = NativeLinux.getuid();
        var gid = NativeLinux.getgid();
        var inventory = SafeTreeDeletion.CaptureChildInventory(versionsRoot, tombstoneName, uid, gid);
        var daemon = new DockerDaemonIdentity("daemon", "host", "amd64", "29.7.2");
        var request = new LifecycleRequest(
            LifecycleOperationKind.CatalogGarbageCollect, null, root, dryRun: false, resume: true, json: false)
        {
            AllowTestProductRoot = true,
            CatalogVersion = version
        };
        var catalog = new CatalogInstallationIdentity(
            HygV42.CatalogId, version, "2", "3", new string('a', 64), 1, 1,
            paths.CatalogRoot, new string('b', 64), "test");
        await SafeFileSystem.WriteJsonAtomicAsync(
            Path.Combine(paths.OperationsRoot, $"catalog-gc-{operationId:N}.json"),
            new CatalogGarbageCollectionEvidence(
                DeploymentSchemaVersions.LifecycleOperation, operationId, request.ComputeRequestSha256(),
                LocalHostIdentity.ReadSha256(), daemon, version, catalog, inventory),
            DeploymentJsonContext.Default.CatalogGarbageCollectionEvidence,
            CancellationToken.None);
        try
        {
            var wrongRequest = request with { CatalogVersion = "hyg-v4.2-p3-s2-r3" };
            await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                wrongRequest, new FakeRunner(daemon), null, null, uid, gid, CancellationToken.None));
            Assert.IsTrue(Directory.Exists(tombstone));

            File.Delete(Path.Combine(tombstone, "remaining"));
            var result = await CameraAgentLifecycleManager.ExecuteAsync(
                request, new FakeRunner(daemon), null, null, uid, gid, CancellationToken.None);

            Assert.AreEqual(operationId, result.OperationId);
            Assert.IsFalse(Directory.Exists(tombstone));

            var postDeleteOperationId = Guid.NewGuid();
            await SafeFileSystem.WriteJsonAtomicAsync(
                Path.Combine(paths.OperationsRoot, $"catalog-gc-{postDeleteOperationId:N}.json"),
                new CatalogGarbageCollectionEvidence(
                    DeploymentSchemaVersions.LifecycleOperation, postDeleteOperationId, request.ComputeRequestSha256(),
                    LocalHostIdentity.ReadSha256(), daemon, version, catalog, inventory, DeletionCommitted: true),
                DeploymentJsonContext.Default.CatalogGarbageCollectionEvidence,
                CancellationToken.None);

            result = await CameraAgentLifecycleManager.ExecuteAsync(
                request, new FakeRunner(daemon), null, null, uid, gid, CancellationToken.None);
            Assert.AreEqual(postDeleteOperationId, result.OperationId);

            var preTombstoneOperationId = Guid.NewGuid();
            var original = Path.Combine(versionsRoot, version);
            Directory.CreateDirectory(original);
            File.SetUnixFileMode(original, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await File.WriteAllTextAsync(Path.Combine(original, "remaining"), "remaining");
            var preTombstoneInventory = SafeTreeDeletion.CaptureChildInventory(versionsRoot, version, uid, gid);
            await SafeFileSystem.WriteJsonAtomicAsync(
                Path.Combine(paths.OperationsRoot, $"catalog-gc-{preTombstoneOperationId:N}.json"),
                new CatalogGarbageCollectionEvidence(
                    DeploymentSchemaVersions.LifecycleOperation, preTombstoneOperationId, request.ComputeRequestSha256(),
                    LocalHostIdentity.ReadSha256(), daemon, version, catalog, preTombstoneInventory),
                DeploymentJsonContext.Default.CatalogGarbageCollectionEvidence,
                CancellationToken.None);

            result = await CameraAgentLifecycleManager.ExecuteAsync(
                request, new FakeRunner(daemon), null, null, uid, gid, CancellationToken.None);
            Assert.AreEqual(preTombstoneOperationId, result.OperationId);
            Assert.IsFalse(Directory.Exists(original));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CatalogInstall_ResumeWithoutRetainedOperationIsRejectedBeforeAcquisition()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-catalog-install-{Guid.NewGuid():N}");
        var daemon = new DockerDaemonIdentity("daemon", "host", "amd64", "29.7.2");
        var request = new LifecycleRequest(
            LifecycleOperationKind.CatalogInstall, null, root, dryRun: false, resume: true, json: false)
        {
            AllowTestProductRoot = true,
            CatalogBundle = Path.Combine(root, "missing-bundle")
        };
        try
        {
            var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                request, new FakeRunner(daemon), null, null, NativeLinux.getuid(), NativeLinux.getgid(), CancellationToken.None));
            StringAssert.Contains(exception.Message, "No matching incomplete", StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void CatalogSelectionResume_AllowsOnlyMatchingMutatedCandidate()
    {
        var instanceId = Guid.NewGuid();
        var request = new LifecycleRequest(
            LifecycleOperationKind.CatalogSelect, instanceId, InstallRequest.DefaultProductRoot,
            dryRun: false, resume: true, json: false)
        {
            CatalogVersion = "hyg-v4.2-p3-s2-r2"
        };
        var image = new ImageInstallationIdentity(
            "registry", $"cameraagent@sha256:{new string('a', 64)}", $"sha256:{new string('b', 64)}", "amd64", null);
        var original = new CatalogInstallationIdentity(
            HygV42.CatalogId, HygV42.PackageVersion, "2", "3", new string('c', 64), 1, 1,
            "/catalog", new string('d', 64), "test");
        var candidate = original with { PackageVersion = request.CatalogVersion };
        var operation = new LifecycleOperationState(
            DeploymentSchemaVersions.LifecycleOperation, Guid.NewGuid(), LifecycleOperationKind.CatalogSelect,
            instanceId, request.ComputeRequestSha256(), LifecycleOperationPhase.Mutating, InstallationStatus.Running,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, image, CandidateCatalog: candidate,
            OriginalCatalog: original, MutationStarted: true);

        Assert.AreEqual(candidate.PackageVersion,
            CameraAgentLifecycleManager.ResumableCatalogSelectionVersion(request, operation));
        Assert.IsNull(CameraAgentLifecycleManager.ResumableCatalogSelectionVersion(request with { Resume = false }, operation));
        Assert.IsNull(CameraAgentLifecycleManager.ResumableCatalogSelectionVersion(
            request, operation with { Kind = LifecycleOperationKind.Upgrade }));
        Assert.IsNull(CameraAgentLifecycleManager.ResumableCatalogSelectionVersion(
            request, operation with { Status = InstallationStatus.Completed }));
    }

    [TestMethod]
    public void CatalogTransition_RequiresImageDeclaringCatalogIndependentReplay()
    {
        var declared = new LifecycleContinuity("Paused", 2, 1812, 0, 0, 0, 0, 0, 0, 0, 0,
            ReplayPending: 3, ReplayLeased: 1, ReplayCatalogIndependent: true);
        CatalogLifecycleManager.EnsureReplayCatalogIndependent(declared, LifecycleOperationKind.CatalogSelect);
        CatalogLifecycleManager.EnsureReplayCatalogIndependent(declared, LifecycleOperationKind.CatalogRollback);

        // The declaration is refused even with no replay visible: the summary counts are a periodic refresh, so an
        // empty count cannot prove nothing was accepted under the current catalog.
        var undeclared = declared with { ReplayPending = 0, ReplayLeased = 0, ReplayCatalogIndependent = false };
        var selection = Assert.ThrowsExactly<InstallerException>(() =>
            CatalogLifecycleManager.EnsureReplayCatalogIndependent(undeclared, LifecycleOperationKind.CatalogSelect));
        StringAssert.Contains(selection.Message, "Catalog selection refused", StringComparison.Ordinal);
        StringAssert.Contains(selection.Message, "does not declare catalog-independent archived replay", StringComparison.Ordinal);
        var rollback = Assert.ThrowsExactly<InstallerException>(() =>
            CatalogLifecycleManager.EnsureReplayCatalogIndependent(undeclared, LifecycleOperationKind.CatalogRollback));
        StringAssert.Contains(rollback.Message, "Catalog rollback refused", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RetainedResult_WithoutTheDefaultPasswordFlag_RemainsValidAndReadsAsNotDefault()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(fixture.Paths.ResultPath))!.AsObject();
        Assert.IsTrue(json.Remove("ownerPasswordIsDefault"), "The result did not record whether the owner password is the default.");
        SafeFileSystem.WriteTextAtomic(fixture.Paths.ResultPath, json.ToJsonString());

        // A result written before issue #1278 omits the flag; it reads as a generated or supplied password.
        var legacy = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        json["ownerPasswordIsDefault"] = true;
        SafeFileSystem.WriteTextAtomic(fixture.Paths.ResultPath, json.ToJsonString());
        var current = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);

        Assert.IsFalse(legacy.OwnerPasswordIsDefault);
        Assert.IsTrue(current.OwnerPasswordIsDefault);
        Assert.AreEqual(legacy with { OwnerPasswordIsDefault = true }, current);
        Assert.AreEqual($"Temporary password file: {legacy.PasswordFile}", Program.OwnerSignInInstruction(legacy));
        Assert.AreEqual(
            "Sign in as admin / skymonitor; a password change is required at first sign-in.",
            Program.OwnerSignInInstruction(current));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CatalogSelection_ImageWithoutCatalogIndependentReplay_RefusesBeforeMutation()
    {
        // Bundle-free coverage of the refusal inside the selection itself, so a lane without the production bundle
        // still fails if the check is removed or moved after the pause. The candidate is synthetic: after candidate
        // validation the selection reads only its identity, never its files, until the check has passed.
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        const string candidateVersion = "hyg-v4.2-p3-s2-r2";
        var request = fixture.Request(LifecycleOperationKind.CatalogSelect) with { CatalogVersion = candidateVersion };
        var candidate = fixture.Manifest.Catalog with { PackageVersion = candidateVersion };
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        var operation = await CameraAgentLifecycleManager.BeginAsync(
            request, fixture.Paths, LifecycleOperationKind.CatalogSelect, fixture.Manifest, CancellationToken.None);
        var undeclared = new FakeLifecycleClient();
        undeclared.RecoveryContinuity = undeclared.RecoveryContinuity with { ReplayCatalogIndependent = false };

        var refusal = await Assert.ThrowsExactlyAsync<InstallerException>(() => CatalogLifecycleManager.SelectAsync(
            request, fixture.Paths, fixture.Manifest, result,
            CameraAgentLifecycleManager.ComposeFrom(fixture.Paths, fixture.Manifest, result), new DockerClient(fixture.Runner),
            _ => undeclared, _ => new FakeOwnerClient(fixture.ApplicationIdentity), "lifecycle-token", "verification-token",
            candidate, operation, CancellationToken.None));
        StringAssert.Contains(refusal.Message, "does not declare catalog-independent archived replay", StringComparison.Ordinal);
        Assert.AreEqual(fixture.Manifest.Catalog.PackageVersion, await File.ReadAllTextAsync(
            Path.Combine(fixture.Paths.ConfigRoot, "secrets", "Catalog__RequiredPackageVersion")));
        Assert.AreEqual(0, undeclared.PauseCount);
        Assert.AreEqual(0, fixture.Runner.ComposeUpCount);
        Assert.IsFalse(File.Exists(Path.Combine(
            fixture.Paths.OperationsRoot, "lifecycle", operation.OperationId.ToString("D"), "previous-catalog-version")));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CatalogSelectAndRollback_CurrentContractsConverge()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed, seedCatalogSelection: false);
        // Deliberately environment-gated; see the note in InstallerFlowTests. The full-qualification Unit lane in
        // ci.yml restores the catalog bundle and asserts it exists before exporting this variable, so a skip there
        // means the restore broke. Development v1 Build and Unit does not restore the bundle and skips this test;
        // CatalogSelection_ImageWithoutCatalogIndependentReplay_RefusesBeforeMutation covers the refusal there.
        var bundle = Environment.GetEnvironmentVariable("HVO_PRODUCTION_CATALOG_BUNDLE");
        if (string.IsNullOrEmpty(bundle)) Assert.Inconclusive("Set HVO_PRODUCTION_CATALOG_BUNDLE to run catalog transitions.");
        var selected = CatalogInstaller.Install(bundle, fixture.Paths.CatalogRoot, Guid.NewGuid());
        var selectedReferenceRoot = Path.Combine(fixture.Paths.CatalogReferencesRoot, selected.PackageVersion);
        Directory.CreateDirectory(selectedReferenceRoot);
        await SafeFileSystem.WriteJsonAtomicAsync(
            Path.Combine(selectedReferenceRoot, "installed.json"), selected,
            DeploymentJsonContext.Default.CatalogInstallationIdentity, CancellationToken.None);

        const string previousVersion = "hyg-v4.2-p3-s2-r2";
        var selectedVersionRoot = Path.Combine(fixture.Paths.CatalogRoot, "versions", selected.PackageVersion);
        var previousVersionRoot = Path.Combine(fixture.Paths.CatalogRoot, "versions", previousVersion);
        Directory.CreateDirectory(previousVersionRoot);
        foreach (var path in Directory.EnumerateFiles(selectedVersionRoot))
            File.Copy(path, Path.Combine(previousVersionRoot, Path.GetFileName(path)));
        var previousManifestPath = Path.Combine(previousVersionRoot, "manifest.json");
        File.SetUnixFileMode(previousManifestPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var previousManifestJson = await File.ReadAllTextAsync(previousManifestPath);
        await File.WriteAllTextAsync(
            previousManifestPath, previousManifestJson.Replace(selected.PackageVersion, previousVersion, StringComparison.Ordinal));
        var previousSnapshot = CatalogSnapshotResolver.Resolve(
            ProductionCatalog.ResolverOptions(fixture.Paths.CatalogRoot, HygV42.CatalogId, previousVersion));
        var previous = ProductionCatalog.ToIdentity(previousSnapshot, fixture.Paths.CatalogRoot);
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.ManifestPath, fixture.Manifest with { Catalog = previous },
            DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.ResultPath, result with { Catalog = previous },
            DeploymentJsonContext.Default.InstallationResult, CancellationToken.None);
        SafeFileSystem.WriteTextAtomic(
            Path.Combine(fixture.Paths.ConfigRoot, "secrets", "Catalog__RequiredPackageVersion"), previous.PackageVersion);
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid);

        // An image that does not declare catalog-independent archived replay may annotate accepted replay from the newly
        // mounted catalog, so the selection is refused before the setting or the container changes.
        var settingPath = Path.Combine(fixture.Paths.ConfigRoot, "secrets", "Catalog__RequiredPackageVersion");
        var undeclared = new FakeLifecycleClient();
        undeclared.RecoveryContinuity = undeclared.RecoveryContinuity with { ReplayCatalogIndependent = false };
        var refusal = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.CatalogSelect) with { CatalogVersion = selected.PackageVersion },
            fixture.Runner, _ => undeclared, _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid,
            CancellationToken.None));
        StringAssert.Contains(refusal.Message, "does not declare catalog-independent archived replay", StringComparison.Ordinal);
        Assert.AreEqual(previous.PackageVersion, await File.ReadAllTextAsync(settingPath));
        Assert.AreEqual(0, undeclared.PauseCount);
        Assert.AreEqual(0, fixture.Runner.ComposeUpCount);

        // Archived replay never builds a scene from the mounted catalog; a retained annotation node with neither a
        // projected-scene dependency nor scene provenance recorded with the frame fails closed in replay. A switch cannot
        // change what accepted replay produces, so replay pending or leased at the boundary does not refuse the selection
        // the way uncommitted live work does.
        var selectedResult = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.CatalogSelect) with { CatalogVersion = selected.PackageVersion },
            fixture.Runner, _ => new FakeLifecycleClient { DrainedReplayPending = 2, DrainedReplayLeased = 1 },
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual(selected.PackageVersion, selectedResult.Catalog!.PackageVersion);

        var rollbackResult = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.CatalogRollback), fixture.Runner,
            _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual(previous.PackageVersion, rollbackResult.Catalog!.PackageVersion);
        Assert.AreEqual(2, fixture.Runner.ComposeUpCount);
        Assert.AreEqual(2, fixture.Runner.ComposeRestartCount);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CatalogSelect_CrossLineage_SelectsSideBySideRootAndRollsBackExactly()
    {
        // Environment-gated like the single-lineage transition. The HYG 4.4 bundle is retained as local evidence
        // until its publication is approved, so CI restores only the 4.2 bundle and this transition skips there.
        var originalBundle = Environment.GetEnvironmentVariable("HVO_PRODUCTION_CATALOG_BUNDLE");
        var candidateBundle = Environment.GetEnvironmentVariable("HVO_HYG_V44_CATALOG_BUNDLE");
        if (string.IsNullOrEmpty(originalBundle) || string.IsNullOrEmpty(candidateBundle))
            Assert.Inconclusive("Set HVO_PRODUCTION_CATALOG_BUNDLE and HVO_HYG_V44_CATALOG_BUNDLE to run cross-lineage transitions.");
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed, seedCatalogSelection: false);
        const string candidateCatalogId = "hyg-v44-production";
        var candidatePaths = fixture.Paths.WithCatalog(candidateCatalogId);
        var original = CatalogInstaller.Install(originalBundle, fixture.Paths.CatalogRoot, Guid.NewGuid());
        var candidate = CatalogInstaller.Install(candidateBundle, candidatePaths.CatalogRoot, Guid.NewGuid());
        Assert.AreEqual(candidateCatalogId, candidate.CatalogId);
        foreach (var (lineagePaths, identity) in new[] { (fixture.Paths, original), (candidatePaths, candidate) })
        {
            var referenceRoot = Path.Combine(lineagePaths.CatalogReferencesRoot, identity.PackageVersion);
            Directory.CreateDirectory(referenceRoot);
            await SafeFileSystem.WriteJsonAtomicAsync(
                Path.Combine(referenceRoot, "installed.json"), identity,
                DeploymentJsonContext.Default.CatalogInstallationIdentity, CancellationToken.None);
        }
        var originalRoot = Fingerprint(fixture.Paths.CatalogRoot);
        var legacyImage = fixture.Manifest.Image;
        var image = legacyImage with { CatalogContract = "hvo-approved-catalogs-v1" };
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        var secrets = Path.Combine(fixture.Paths.ConfigRoot, "secrets");
        var environmentPath = Path.Combine(fixture.Paths.ConfigRoot, "compose", "instance.env");
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid);
        var request = fixture.Request(LifecycleOperationKind.CatalogSelect) with { CatalogVersion = candidate.PackageVersion };

        // An image that declares only the 4.2 lineage cannot resolve the 4.4 catalog, so the selection is refused
        // before any journal entry, secret, or environment change.
        await WriteSelectionAsync(legacyImage, previousImage: null);
        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
        Assert.AreEqual(HygV42.CatalogId, await File.ReadAllTextAsync(Path.Combine(secrets, "Catalog__RequiredCatalogId")));
        Assert.AreEqual(0, fixture.Runner.ComposeUpCount);

        await WriteSelectionAsync(image, image with { ImageId = $"sha256:{new string('6', 64)}" });
        // A 4.2 capture whose processing has not committed would be re-annotated under 4.4 on retry, so the
        // selection is refused at the drained boundary and the exact 4.2 selection is restored and resumed.
        var pendingSelection = new FakeLifecycleClient { DrainedProcessingPending = 3 };
        var selectionRefusal = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => pendingSelection, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));
        StringAssert.StartsWith(selectionRefusal.Message,
            "Catalog selection refused: CameraAgent still has 3 capture-processing item(s) pending or awaiting retry",
            StringComparison.Ordinal);
        StringAssert.EndsWith(selectionRefusal.Message, "The prior exact selection was restored.", StringComparison.Ordinal);
        Assert.AreEqual(1, pendingSelection.ResumeCount);
        Assert.AreEqual(original, (await ReadManifestAsync()).Catalog);
        Assert.AreEqual(HygV42.CatalogId, await File.ReadAllTextAsync(Path.Combine(secrets, "Catalog__RequiredCatalogId")));
        Assert.AreEqual(original.PackageVersion, await File.ReadAllTextAsync(Path.Combine(secrets, "Catalog__RequiredPackageVersion")));
        CollectionAssert.Contains(await File.ReadAllLinesAsync(environmentPath), $"HVO_CATALOG_ROOT={fixture.Paths.CatalogRoot}");

        var selected = await CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual(candidate, selected.Catalog);
        Assert.AreEqual(candidateCatalogId, await File.ReadAllTextAsync(Path.Combine(secrets, "Catalog__RequiredCatalogId")));
        Assert.AreEqual(candidate.PackageVersion, await File.ReadAllTextAsync(Path.Combine(secrets, "Catalog__RequiredPackageVersion")));
        CollectionAssert.Contains(await File.ReadAllLinesAsync(environmentPath), $"HVO_CATALOG_ROOT={candidatePaths.CatalogRoot}");
        var committed = await ReadManifestAsync();
        Assert.AreEqual(candidate, committed.Catalog);
        Assert.AreEqual(original, committed.PreviousCatalog);
        // The retained image rollback is bound to the 4.2 root, so crossing lineages retires it.
        Assert.IsNull(committed.PreviousImage);
        Assert.IsNull(committed.PreviousComposeModelSha256);
        Assert.IsTrue(File.Exists(Path.Combine(
            fixture.Paths.CatalogReferencesRoot, original.PackageVersion, "historical", $"cameraagent-{fixture.InstanceId:D}.json")));
        CollectionAssert.AreEquivalent(originalRoot, Fingerprint(fixture.Paths.CatalogRoot));

        // The reverse: 4.4 processing work awaiting retry refuses the rollback and keeps 4.4 selected.
        var pendingRollback = new FakeLifecycleClient { DrainedProcessingPending = 1 };
        var rollbackRefusal = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.CatalogRollback), fixture.Runner,
            _ => pendingRollback, _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None));
        StringAssert.StartsWith(rollbackRefusal.Message,
            "Catalog rollback refused: CameraAgent still has 1 capture-processing item(s) pending or awaiting retry",
            StringComparison.Ordinal);
        Assert.AreEqual(1, pendingRollback.ResumeCount);
        Assert.AreEqual(candidate, (await ReadManifestAsync()).Catalog);
        Assert.AreEqual(candidateCatalogId, await File.ReadAllTextAsync(Path.Combine(secrets, "Catalog__RequiredCatalogId")));
        CollectionAssert.Contains(await File.ReadAllLinesAsync(environmentPath), $"HVO_CATALOG_ROOT={candidatePaths.CatalogRoot}");

        var rolledBack = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.CatalogRollback), fixture.Runner,
            _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual(original, rolledBack.Catalog);
        Assert.AreEqual(HygV42.CatalogId, await File.ReadAllTextAsync(Path.Combine(secrets, "Catalog__RequiredCatalogId")));
        Assert.AreEqual(original.PackageVersion, await File.ReadAllTextAsync(Path.Combine(secrets, "Catalog__RequiredPackageVersion")));
        CollectionAssert.Contains(await File.ReadAllLinesAsync(environmentPath), $"HVO_CATALOG_ROOT={fixture.Paths.CatalogRoot}");
        var restored = await ReadManifestAsync();
        Assert.AreEqual(original, restored.Catalog);
        Assert.AreEqual(candidate, restored.PreviousCatalog);
        Assert.IsTrue(File.Exists(Path.Combine(
            candidatePaths.CatalogReferencesRoot, candidate.PackageVersion, "historical", $"cameraagent-{fixture.InstanceId:D}.json")));
        CollectionAssert.AreEquivalent(originalRoot, Fingerprint(fixture.Paths.CatalogRoot));
        // Each refusal recreates the original selection once during exact restoration; neither restarts a candidate.
        Assert.AreEqual(4, fixture.Runner.ComposeUpCount);
        Assert.AreEqual(2, fixture.Runner.ComposeRestartCount);

        async Task WriteSelectionAsync(ImageInstallationIdentity selectedImage, ImageInstallationIdentity? previousImage)
        {
            fixture.Runner.CatalogContract = selectedImage.CatalogContract!;
            await SafeFileSystem.WriteJsonAtomicAsync(
                fixture.Paths.ManifestPath,
                fixture.Manifest with
                {
                    Catalog = original,
                    Image = selectedImage,
                    PreviousImage = previousImage,
                    PreviousComposeModelSha256 = previousImage is null ? null : fixture.Manifest.ComposeModelSha256
                },
                DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
            await SafeFileSystem.WriteJsonAtomicAsync(
                fixture.Paths.ResultPath, result with { Catalog = original, Image = selectedImage },
                DeploymentJsonContext.Default.InstallationResult, CancellationToken.None);
        }

        async Task<InstanceManifest> ReadManifestAsync()
            => JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(fixture.Paths.ManifestPath), DeploymentJsonContext.Default.InstanceManifest)!;

        static Dictionary<string, string> Fingerprint(string root)
            => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .ToDictionary(
                    path => Path.GetRelativePath(root, path),
                    path => new FileInfo(path).LinkTarget is { } target
                        ? $"link:{target}"
                        : File.Exists(path)
                            ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
                            : "directory");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UninstallAsync_HygV44InstanceUsesItsOwnLineageAndPreservesState()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed, catalogId: "hyg-v44-production");
        StringAssert.EndsWith(fixture.Paths.CatalogRoot, Path.Combine("catalogs", "hyg-v44-production"), StringComparison.Ordinal);
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid);
        var stateSentinel = Path.Combine(fixture.Paths.StateRoot, "sentinel");
        await File.WriteAllTextAsync(stateSentinel, "preserved");
        var lifecycle = new FakeLifecycleClient();

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Uninstall), fixture.Runner, _ => lifecycle, null,
            fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(InstanceLifecycleCondition.Uninstalled, result.LifecycleCondition);
        Assert.AreEqual(fixture.Manifest.Catalog, result.Catalog);
        Assert.IsTrue(File.Exists(stateSentinel));
        Assert.AreEqual(1, fixture.Runner.ComposeDownCount);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Paths.ProductRoot, "catalogs", HygV42.CatalogId)));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RecoverySnapshot_ForeignCandidateIsRejectedWithoutCanonicalOverwrite()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        var candidate = fixture.Manifest.Image with
        {
            ImmutableReference = $"cameraagent@sha256:{new string('7', 64)}",
            ImageId = $"sha256:{new string('6', 64)}"
        };
        var operation = new LifecycleOperationState(
            DeploymentSchemaVersions.LifecycleOperation, Guid.NewGuid(), LifecycleOperationKind.Upgrade,
            fixture.InstanceId, new string('5', 64), LifecycleOperationPhase.Mutating, InstallationStatus.Running,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, fixture.Manifest.Image, candidate,
            fixture.Manifest.Catalog, MutationStarted: true);
        var root = Path.Combine(fixture.Paths.OperationsRoot, "snapshot-test");
        Directory.CreateDirectory(root);
        var snapshotManifestPath = Path.Combine(root, "manifest.json");
        var snapshotResultPath = Path.Combine(root, "result.json");
        await SafeFileSystem.WriteJsonAtomicAsync(
            snapshotManifestPath, fixture.Manifest with { Image = candidate },
            DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(
            snapshotResultPath, result with { Image = candidate },
            DeploymentJsonContext.Default.InstallationResult, CancellationToken.None);
        var canonicalManifest = await File.ReadAllTextAsync(fixture.Paths.ManifestPath);
        var canonicalResult = await File.ReadAllTextAsync(fixture.Paths.ResultPath);

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ReadRecoverySnapshotAsync(
            fixture.Paths, operation, snapshotManifestPath, snapshotResultPath, CancellationToken.None));

        Assert.AreEqual(canonicalManifest, await File.ReadAllTextAsync(fixture.Paths.ManifestPath));
        Assert.AreEqual(canonicalResult, await File.ReadAllTextAsync(fixture.Paths.ResultPath));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_ResumeAfterCommitCompletesWithoutRollingBackCandidate()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('7', 64)}";
        var candidateImageId = $"sha256:{new string('8', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var lifecycle = new FakeLifecycleClient { RejectNextResume = true, DrainedReplayPending = 4, DrainedReplayLeased = 1 };
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None));

        var committedManifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(candidateImageId, committedManifest.Image.ImageId);
        Assert.AreEqual(LifecycleOperationPhase.Committed, retained!.Phase);
        Assert.IsTrue(retained.MutationStarted);
        Assert.IsNotNull(retained.PreMutationContinuity);
        Assert.IsNotNull(retained.PostMutationContinuity);
        // The drain does not wait for archived replay, so the boundary records what it left behind.
        Assert.AreEqual(4, retained.PreMutationContinuity.ReplayPending);
        Assert.AreEqual(1, retained.PreMutationContinuity.ReplayLeased);

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            request with { Resume = true }, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        // One durable pause per operation; the post-mutation boundary only confirms it.
        Assert.AreEqual(1, lifecycle.PauseCount);
        Assert.AreEqual(1, lifecycle.ConfirmCount);
        retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationPhase.Completed, retained!.Phase);
        Assert.IsFalse(retained.MutationStarted);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_ReleaseResumeAfterLostAcknowledgementRetainsReleaseRecord()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        using var release = CandidateRelease(fixture, out var candidateImageId);
        fixture.Runner.CandidateRawIngressSchema = "13";
        var lifecycle = new FakeLifecycleClient { RejectNextResume = true };
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            Release = release.Folder,
            MigrationBackwardCompatible = true
        };
        var evidencePath = Path.Combine(fixture.Paths.DeploymentStateRoot, "image-distribution.json");

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        // The record is settled with the durable commit, before the resume whose acknowledgement was lost.
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationPhase.Committed, retained!.Phase);
        Assert.AreEqual(candidateImageId, await ReadEvidenceImageIdAsync(evidencePath));

        // A failure between the commit and the record write leaves no record; the resumed run must repair it rather
        // than return completed while the instance runs a release the retained evidence does not name.
        File.Delete(evidencePath);
        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            request with { Resume = true }, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(candidateImageId, result.Image!.ImageId);
        Assert.AreEqual(candidateImageId, await ReadEvidenceImageIdAsync(evidencePath));
        retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationPhase.Completed, retained!.Phase);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_ImageReferenceUpgradeOfReleaseInstanceWithdrawsReleaseRecord()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        using var release = CandidateRelease(fixture, out var signedImageId);
        fixture.Runner.CandidateRawIngressSchema = "13";
        var lifecycle = new FakeLifecycleClient();
        var evidencePath = Path.Combine(fixture.Paths.DeploymentStateRoot, "image-distribution.json");

        var signed = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                Release = release.Folder,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", signed.Outcome);
        Assert.AreEqual(signedImageId, await ReadEvidenceImageIdAsync(evidencePath));

        // Move the instance to an operator-supplied image that no release named.
        var operatorReference = $"ghcr.io/example/cameraagent@sha256:{new string('c', 64)}";
        var operatorImageId = $"sha256:{new string('d', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, signedImageId, signedImageId,
            operatorReference, operatorImageId, fixture.Uid, fixture.Gid);
        var moved = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                ImageReference = operatorReference,
                NoDownload = true,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", moved.Outcome);
        Assert.AreEqual(operatorImageId, moved.Image!.ImageId);
        Assert.IsFalse(File.Exists(evidencePath), "The superseded release record survived an upgrade to an image no release named.");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_ReleaseRefusedBeforeMutationLeavesInstanceOperableWithoutResume()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        using var release = CandidateRelease(fixture, out var candidateImageId);
        // The image the archive loads names another version than the release, so the release-agreement gate refuses
        // it after the journal is opened and before any mutation.
        fixture.Runner.CandidateLabels["org.opencontainers.image.version"] = "9.9.9";
        var lifecycle = new FakeLifecycleClient();
        var evidencePath = Path.Combine(fixture.Paths.DeploymentStateRoot, "image-distribution.json");
        var manifestBefore = await File.ReadAllTextAsync(fixture.Paths.ManifestPath);
        var refused = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            Release = release.Folder,
            MigrationBackwardCompatible = true
        };

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            refused, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(exception.Message, $"is not labelled as CameraAgent {release.Version}", StringComparison.Ordinal);
        Assert.AreEqual(0, lifecycle.PauseCount);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(manifestBefore, await File.ReadAllTextAsync(fixture.Paths.ManifestPath));
        Assert.IsFalse(File.Exists(evidencePath), "A refused release wrote a release record.");
        // The refusal is journalled as terminal: nothing ran, so nothing reports itself as running.
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationKind.Upgrade, retained!.Kind);
        Assert.AreEqual(InstallationStatus.Failed, retained.Status);
        Assert.AreEqual(CameraAgentLifecycleManager.RefusedFailureCode, retained.FailureCode);
        Assert.AreEqual(LifecycleOperationPhase.Prepared, retained.Phase);
        Assert.IsFalse(retained.MutationStarted);
        StringAssert.Contains(retained.FailureMessage!, "is not labelled as CameraAgent", StringComparison.Ordinal);
        var refusedJournal = await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath);

        // Resuming the refused command is refused in turn: there is no incomplete operation to resume.
        var resumeRefusal = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            refused with { Resume = true }, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));
        StringAssert.Contains(resumeRefusal.Message, "No incomplete matching lifecycle operation exists", StringComparison.Ordinal);
        Assert.AreEqual(refusedJournal, await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath));
        // Nor does a --resume of a different kind find anything to continue.
        var uninstallResumeRefusal = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Uninstall) with { Resume = true }, fixture.Runner, _ => lifecycle, null,
            fixture.Uid, fixture.Gid, CancellationToken.None));
        StringAssert.Contains(uninstallResumeRefusal.Message, "No incomplete matching lifecycle operation exists", StringComparison.Ordinal);
        Assert.AreEqual(refusedJournal, await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath));

        // A different upgrade proceeds without --resume and supersedes the refused entry.
        var operatorReference = $"ghcr.io/example/cameraagent@sha256:{new string('c', 64)}";
        var operatorImageId = $"sha256:{new string('d', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            operatorReference, operatorImageId, fixture.Uid, fixture.Gid);
        var upgraded = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                ImageReference = operatorReference,
                NoDownload = true,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", upgraded.Outcome);
        Assert.AreEqual(operatorImageId, upgraded.Image!.ImageId);
        var superseding = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreNotEqual(retained.OperationId, superseding!.OperationId);
        Assert.AreEqual(LifecycleOperationPhase.Completed, superseding.Phase);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RollbackAsync_ProceedsWithoutResumeAfterRefusedUpgrade()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var lifecycle = new FakeLifecycleClient();
        var upgraded = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                ImageReference = candidateReference,
                NoDownload = true,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", upgraded.Outcome);

        // Upgrading to the image that is already active is refused after the journal is opened and before any
        // mutation, the same window the label-agreement gate occupies.
        var refused = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };
        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            refused, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));
        StringAssert.Contains(exception.Message, "already active", StringComparison.Ordinal);
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(InstallationStatus.Failed, retained!.Status);
        Assert.AreEqual(CameraAgentLifecycleManager.RefusedFailureCode, retained.FailureCode);
        Assert.IsFalse(retained.MutationStarted);
        Assert.AreEqual(1, lifecycle.PauseCount);

        var rolledBack = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback), fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", rolledBack.Outcome);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, rolledBack.Image!.ImageId);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(2, lifecycle.PauseCount);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UninstallAsync_ProceedsWithoutResumeAfterRefusedUpgrade()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid);
        var lifecycle = new FakeLifecycleClient();
        var refused = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = fixture.Manifest.Image.ImmutableReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };
        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            refused, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(CameraAgentLifecycleManager.RefusedFailureCode, retained!.FailureCode);

        var uninstalled = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Uninstall), fixture.Runner, _ => lifecycle, null,
            fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", uninstalled.Outcome);
        Assert.AreEqual(InstanceLifecycleCondition.Uninstalled, uninstalled.LifecycleCondition);
        var completed = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationKind.Uninstall, completed!.Kind);
        Assert.AreEqual(LifecycleOperationPhase.Completed, completed.Phase);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task SettleRefusedOperation_ResumedMutatedOperationRestoredBeforeFailureIsNotRefused()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        // The operation as BeginAsync returned it on --resume: it had started mutating before the interruption.
        var begun = new LifecycleOperationState(
            DeploymentSchemaVersions.LifecycleOperation, Guid.NewGuid(), LifecycleOperationKind.Upgrade,
            fixture.InstanceId, new string('5', 64), LifecycleOperationPhase.Mutating, InstallationStatus.Running,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, fixture.Manifest.Image,
            fixture.Manifest.Image with { ImageId = $"sha256:{new string('6', 64)}" }, fixture.Manifest.Catalog,
            MutationStarted: true);
        // The journal after the resumed run restored the original runtime and cleared the flag, immediately before
        // its next mutation record: the only window in which a failure reaches the wrapper on such an operation.
        var restored = begun with { Phase = LifecycleOperationPhase.Prepared, MutationStarted = false };
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.LifecycleStatePath, restored, DeploymentJsonContext.Default.LifecycleOperationState, CancellationToken.None);
        var journal = await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath);

        await CameraAgentLifecycleManager.SettleRefusedOperationAsync(
            fixture.Paths, begun, new IOException("simulated recovery-record write failure"));

        // The journal's current flag is not a history of mutation: the record keeps its recovery semantics.
        Assert.AreEqual(journal, await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath));
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(InstallationStatus.Running, retained!.Status);
        Assert.IsNull(retained.FailureCode);

        // The same journal state behind an operation that never mutated is a refusal.
        await CameraAgentLifecycleManager.SettleRefusedOperationAsync(
            fixture.Paths, restored, new InstallerException("simulated pre-mutation refusal"));
        retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(InstallationStatus.Failed, retained!.Status);
        Assert.AreEqual(CameraAgentLifecycleManager.RefusedFailureCode, retained.FailureCode);
        Assert.IsFalse(retained.MutationStarted);

        // A record that is not the begun operation, or is no longer running, is never touched.
        var journalAfterRefusal = await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath);
        await CameraAgentLifecycleManager.SettleRefusedOperationAsync(
            fixture.Paths, restored with { OperationId = Guid.NewGuid() }, new InstallerException("other"));
        await CameraAgentLifecycleManager.SettleRefusedOperationAsync(
            fixture.Paths, restored, new InstallerException("already failed"));
        Assert.AreEqual(journalAfterRefusal, await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RollbackAsync_ProceedsWithoutResumeAfterLegacyPreMutationRefusalJournal()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var lifecycle = new FakeLifecycleClient();
        var upgraded = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                ImageReference = candidateReference,
                NoDownload = true,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", upgraded.Outcome);
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        // The journal an earlier release left behind for a label-agreement refusal: running, unmutated, no code.
        var legacyRequest = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = $"ghcr.io/example/cameraagent@sha256:{new string('e', 64)}",
            NoDownload = true,
            MigrationBackwardCompatible = true
        };
        var legacy = new LifecycleOperationState(
            DeploymentSchemaVersions.LifecycleOperation, Guid.NewGuid(), LifecycleOperationKind.Upgrade,
            fixture.InstanceId, legacyRequest.ComputeRequestSha256(), LifecycleOperationPhase.Prepared, InstallationStatus.Running,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, manifest.Image, null, manifest.Catalog);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.LifecycleStatePath, legacy, DeploymentJsonContext.Default.LifecycleOperationState, CancellationToken.None);

        var rolledBack = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback), fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", rolledBack.Outcome);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        var superseding = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreNotEqual(legacy.OperationId, superseding!.OperationId);
        Assert.AreEqual(LifecycleOperationKind.Rollback, superseding.Kind);

        // --resume of the matching request still continues an unmutated incomplete record: crash-between-records
        // recovery keeps its operation ID.
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.LifecycleStatePath, legacy with { OriginalImage = fixture.Manifest.Image },
            DeploymentJsonContext.Default.LifecycleOperationState, CancellationToken.None);
        var manifestNow = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var resumed = await CameraAgentLifecycleManager.BeginAsync(
            legacyRequest with { Resume = true }, fixture.Paths, LifecycleOperationKind.Upgrade, manifestNow, CancellationToken.None);
        Assert.AreEqual(legacy.OperationId, resumed.OperationId);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_SettlementFailureNeverReplacesTheRefusal()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        using var release = CandidateRelease(fixture, out var candidateImageId);
        fixture.Runner.CandidateLabels["org.opencontainers.image.version"] = "9.9.9";
        // Once the journal has been opened, replace it with bytes the settlement cannot read, so the settlement's
        // own failure competes with the gate's refusal. (The owner-directory normalisation inside the atomic
        // writer defeats a read-only directory as the fault, so the fault is the retained bytes themselves.)
        const string corrupted = "{";
        fixture.Runner.OnCandidateInspect = () => File.WriteAllText(fixture.Paths.LifecycleStatePath, corrupted);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                Release = release.Folder,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(exception.Message, "is not labelled as CameraAgent", StringComparison.Ordinal);
        // The settlement failed and changed nothing; the journal is exactly what the fault left.
        Assert.AreEqual(corrupted, await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UpgradeAsync_ProtectedReadFailureOnResumeNeverPausesOrMutates(bool callerCancels)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var lifecycle = new FakeLifecycleClient();
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };
        // Reproduce the retained Prepared/Running, MutationStarted=false state using the normal journal writer.
        var begun = await CameraAgentLifecycleManager.BeginAsync(
            request, fixture.Paths, LifecycleOperationKind.Upgrade, fixture.Manifest, CancellationToken.None);
        var manifestBefore = await File.ReadAllTextAsync(fixture.Paths.ManifestPath);
        var resultBefore = await File.ReadAllTextAsync(fixture.Paths.ResultPath);
        var composePath = Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml");
        var environmentPath = Path.Combine(fixture.Paths.ConfigRoot, "compose", "instance.env");
        var composeBefore = await File.ReadAllTextAsync(composePath);
        var environmentBefore = await File.ReadAllTextAsync(environmentPath);
        using var caller = new CancellationTokenSource();
        using var handler = new ScriptedHandler(async (_, _, cancellationToken) =>
        {
            if (callerCancels) await caller.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new AssertFailedException("The protected read must be canceled.");
        });
        var owner = new OwnerBootstrapClient(new Uri("http://verification.invalid"), handler, TimeSpan.FromMilliseconds(100));
        Task<LifecycleResult> ResumeAsync() => CameraAgentLifecycleManager.ExecuteAsync(
            request with { Resume = true }, fixture.Runner, _ => lifecycle, _ => owner,
            fixture.Uid, fixture.Gid, caller.Token);

        if (callerCancels)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(ResumeAsync);
        }
        else
        {
            var exception = await Assert.ThrowsExactlyAsync<InstallerException>(ResumeAsync);
            Assert.AreEqual("CameraAgent installation verification request timed out.", exception.Message);
        }

        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(begun.OperationId, retained!.OperationId);
        Assert.AreEqual(LifecycleOperationPhase.Prepared, retained.Phase);
        Assert.IsFalse(retained.MutationStarted);
        Assert.AreEqual(callerCancels ? InstallationStatus.Running : InstallationStatus.Failed, retained.Status);
        Assert.AreEqual(callerCancels ? null : CameraAgentLifecycleManager.RefusedFailureCode, retained.FailureCode);
        Assert.IsNull(retained.BackupManifestSha256);
        Assert.AreEqual(0, fixture.Runner.BackupCount);
        Assert.AreEqual(0, lifecycle.PauseCount);
        Assert.AreEqual(0, fixture.Runner.ComposeStopCount);
        Assert.AreEqual(0, fixture.Runner.ComposeUpCount);
        Assert.AreEqual(0, fixture.Runner.ComposeDownCount);
        Assert.AreEqual(0, fixture.Runner.ComposeRestartCount);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(manifestBefore, await File.ReadAllTextAsync(fixture.Paths.ManifestPath));
        Assert.AreEqual(resultBefore, await File.ReadAllTextAsync(fixture.Paths.ResultPath));
        Assert.AreEqual(composeBefore, await File.ReadAllTextAsync(composePath));
        Assert.AreEqual(environmentBefore, await File.ReadAllTextAsync(environmentPath));
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_CancellationBeforeMutationIsNotRefusedAndDoesNotBlock()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.OnCandidateInspect = () => throw new OperationCanceledException("simulated cancellation during preparation");
        var lifecycle = new FakeLifecycleClient();
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(InstallationStatus.Running, retained!.Status);
        Assert.IsNull(retained.FailureCode);
        Assert.IsFalse(retained.MutationStarted);

        // The cancelled, unmutated operation is resumable and blocks nothing else.
        fixture.Runner.OnCandidateInspect = null;
        var uninstalled = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Uninstall), fixture.Runner, _ => lifecycle, null,
            fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", uninstalled.Outcome);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CatalogGarbageCollection_RefusedSelectionHoldsNoReference()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        const string candidateVersion = "hyg-v4.2-p3-s2-r2";
        var candidate = fixture.Manifest.Catalog with { PackageVersion = candidateVersion };
        var refused = new LifecycleOperationState(
            DeploymentSchemaVersions.LifecycleOperation, Guid.NewGuid(), LifecycleOperationKind.CatalogSelect,
            fixture.InstanceId, new string('5', 64), LifecycleOperationPhase.CandidateValidated, InstallationStatus.Failed,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, fixture.Manifest.Image, null, fixture.Manifest.Catalog, candidate,
            FailureCode: CameraAgentLifecycleManager.RefusedFailureCode, FailureMessage: "refused");
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.LifecycleStatePath, refused, DeploymentJsonContext.Default.LifecycleOperationState, CancellationToken.None);

        Assert.IsFalse(await CatalogLifecycleManager.IsReferencedAsync(fixture.Paths, candidateVersion, CancellationToken.None));

        // The same record while it was still running did hold the reference.
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.LifecycleStatePath, refused with { Status = InstallationStatus.Running, FailureCode = null, FailureMessage = null },
            DeploymentJsonContext.Default.LifecycleOperationState, CancellationToken.None);
        Assert.IsTrue(await CatalogLifecycleManager.IsReferencedAsync(fixture.Paths, candidateVersion, CancellationToken.None));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ReadOperationAsync_RefusedRecordThatClaimsMutationIsRejected()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var refused = new LifecycleOperationState(
            DeploymentSchemaVersions.LifecycleOperation, Guid.NewGuid(), LifecycleOperationKind.Upgrade,
            fixture.InstanceId, new string('5', 64), LifecycleOperationPhase.Prepared, InstallationStatus.Failed,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, fixture.Manifest.Image, null, fixture.Manifest.Catalog,
            FailureCode: CameraAgentLifecycleManager.RefusedFailureCode, FailureMessage: "refused");
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.LifecycleStatePath, refused, DeploymentJsonContext.Default.LifecycleOperationState, CancellationToken.None);
        Assert.IsNotNull(await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None));

        foreach (var impossible in new[]
                 {
                     refused with { MutationStarted = true },
                     refused with { Status = InstallationStatus.Running }
                 })
        {
            await SafeFileSystem.WriteJsonAtomicAsync(
                fixture.Paths.LifecycleStatePath, impossible, DeploymentJsonContext.Default.LifecycleOperationState, CancellationToken.None);
            await Assert.ThrowsExactlyAsync<InstallerException>(() =>
                CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None));
        }
    }

    /// <summary>
    /// A release of <paramref name="version"/> whose image, for the instance's recorded daemon platform, is the candidate
    /// the fake daemon loads from its archive: the archive's configuration carries the labels the fake daemon reports for
    /// the candidate, with the release's version and commit, and its digest is the candidate's image ID, which
    /// <paramref name="candidateImageId"/> returns. A test that changes the fake's candidate labels afterwards makes Docker
    /// report an image other than the one the archive declares.
    /// </summary>
    private static ReleaseFixture CandidateRelease(
        LifecycleFixture fixture,
        out string candidateImageId,
        IReadOnlyList<string>? platforms = null,
        IReadOnlyList<(string Version, string Text)>? notes = null,
        string version = "1.2.0-dev.3",
        Action<Dictionary<string, string>>? labels = null)
    {
        fixture.Runner.CandidateLabels["org.opencontainers.image.version"] = version;
        fixture.Runner.CandidateLabels["org.opencontainers.image.revision"] = ReleaseFixture.Commit;
        fixture.Runner.CandidateLabels["org.opencontainers.image.created"] = ReleaseFixture.Created;
        var archived = fixture.Runner.CandidateImageLabels();
        labels?.Invoke(archived);
        var release = ReleaseFixture.Create(
            fixture.Root,
            version: version,
            platforms: platforms,
            upgradeNotes: notes,
            labels: archived);
        candidateImageId = release.ConfigDigest($"linux/{fixture.Manifest.DockerDaemon.Architecture}");
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateImageId, candidateImageId, fixture.Uid, fixture.Gid);
        return release;
    }

    private static async Task<string?> ReadEvidenceImageIdAsync(string evidencePath)
    {
        using var evidence = JsonDocument.Parse(await File.ReadAllBytesAsync(evidencePath));
        return evidence.RootElement.GetProperty("imageId").GetString();
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ReinstallAsync_PreservedInstanceVerifiesOwnerBeforeCommit()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Uninstalled);
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid,
            exists: false);
        var lifecycle = new FakeLifecycleClient();

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Reinstall),
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(InstanceLifecycleCondition.Installed, result.LifecycleCondition);
        var operation = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.IsNotNull(operation!.PostMutationContinuity);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ReinstallAsync_DaemonReplacementIsRejectedBeforeComposeUp()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Uninstalled);
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid,
            exists: false);
        fixture.Runner.DaemonDriftAtInfoCall = 3;

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Reinstall), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.AreEqual(0, fixture.Runner.ComposeUpCount);
        Assert.IsNull(fixture.Runner.ActiveImageId);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_DaemonReplacementIsRejectedBeforeRestart()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('d', 64)}";
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, $"sha256:{new string('e', 64)}", fixture.Uid, fixture.Gid);
        fixture.Runner.DriftDaemonAfterComposeUp = true;

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                ImageReference = candidateReference,
                NoDownload = true,
                MigrationBackwardCompatible = true
            }, fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.AreEqual(0, fixture.Runner.ComposeRestartCount);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_FailedCandidateRestoresExactOriginalRuntime()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true };
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => new FakeLifecycleClient(), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None));

        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, manifest.Image.ImageId);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId, failure.ToString());
        Assert.AreEqual(InstallationStatus.Failed, retained!.Status);
        Assert.IsFalse(retained.MutationStarted);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_OwnerStateRegressionRestoresExactOriginalRuntime()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { CurrentOwnerBootstrapState = "owner-ready" };
        owner.VerificationStates.Enqueue("owner-password-change-required");
        owner.VerificationStates.Enqueue("owner-ready");
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => new FakeLifecycleClient(), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None));

        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(
            fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual("owner-ready", retained!.ExpectedOwnerBootstrapState);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(InstallationStatus.Failed, retained.Status);
        Assert.IsFalse(retained.MutationStarted);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_OwnerCompletesPasswordReplacementDuringMutation_Converges()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity);
        owner.VerificationStates.Enqueue("owner-ready");
        owner.VerificationStates.Enqueue("owner-ready");
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => new FakeLifecycleClient(), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(candidateImageId, result.Image!.ImageId);
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(
            fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual("owner-ready", retained!.ExpectedOwnerBootstrapState);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_OwnerStateRegressesAfterCandidateRestart_RestoresOriginalRuntime()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity);
        owner.VerificationStates.Enqueue("owner-ready");
        owner.VerificationStates.Enqueue("owner-password-change-required");
        owner.VerificationStates.Enqueue("owner-ready");
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => new FakeLifecycleClient(), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None));

        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(
            fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual("owner-ready", retained!.ExpectedOwnerBootstrapState);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(InstallationStatus.Failed, retained.Status);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_InterruptedMutationPersistsRestoredOwnerProgressionBeforeRetry()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true };
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => new FakeLifecycleClient(), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None));
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(
            fixture.Paths.LifecycleStatePath, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.LifecycleStatePath,
            retained! with
            {
                Phase = LifecycleOperationPhase.Mutating,
                Status = InstallationStatus.Running,
                MutationStarted = true,
                FailureCode = null,
                FailureMessage = null,
                ExpectedOwnerBootstrapState = "owner-password-change-required"
            },
            DeploymentJsonContext.Default.LifecycleOperationState,
            CancellationToken.None);
        owner.VerificationStates.Enqueue("owner-ready");
        owner.VerificationStates.Enqueue("owner-password-change-required");
        owner.VerificationStates.Enqueue("owner-ready");

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request with { Resume = true }, fixture.Runner, _ => new FakeLifecycleClient(), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None));

        retained = await CameraAgentLifecycleManager.ReadOperationAsync(
            fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual("owner-ready", retained!.ExpectedOwnerBootstrapState);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(InstallationStatus.Failed, retained.Status);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_PreparedOwnerStateChangeIsRejectedWithoutOverwritingSnapshot()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.RejectNextStop = true;
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { CurrentOwnerBootstrapState = "owner-ready" };
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => new FakeLifecycleClient(), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None));
        owner.CurrentOwnerBootstrapState = "owner-password-change-required";

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request with { Resume = true }, fixture.Runner, _ => new FakeLifecycleClient(), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None));

        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(
            fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual("owner-ready", retained!.ExpectedOwnerBootstrapState);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_FailedLocalRunnerCandidateCapturesBothContainerLogsBeforeRollback()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var candidateImageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(
            fixture.Paths,
            fixture.Manifest.Image.ImmutableReference,
            fixture.Manifest.Image.ImageId,
            candidateReference,
            candidateImageId,
            fixture.Uid,
            fixture.Gid);
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                ImageReference = candidateReference,
                NoDownload = true,
                MigrationBackwardCompatible = true
            },
            fixture.Runner,
            _ => new FakeLifecycleClient(),
            _ => owner,
            fixture.Uid,
            fixture.Gid,
            CancellationToken.None));

        var compact = fixture.InstanceId.ToString("N");
        var cameraAgentContainer = $"hvo-skymonitor-{compact}";
        var replayRunnerContainer = $"{cameraAgentContainer}-replay";
        CollectionAssert.AreEqual(
            new[] { cameraAgentContainer, replayRunnerContainer },
            fixture.Runner.LoggedContainers.ToArray());
        var firstRecoveryUp = fixture.Runner.Events.FindLastIndex(static value => value == "compose-up");
        Assert.IsGreaterThan(fixture.Runner.Events.IndexOf($"logs:{cameraAgentContainer}"), firstRecoveryUp);
        Assert.IsGreaterThan(fixture.Runner.Events.IndexOf($"logs:{replayRunnerContainer}"), firstRecoveryUp);

        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(
            fixture.Paths.LifecycleStatePath,
            CancellationToken.None);
        var diagnostics = await File.ReadAllTextAsync(Path.Combine(
            fixture.Paths.OperationsRoot,
            "lifecycle",
            retained!.OperationId.ToString("D"),
            "candidate-diagnostics.txt"));
        StringAssert.Contains(diagnostics, $"container={cameraAgentContainer}", StringComparison.Ordinal);
        StringAssert.Contains(diagnostics, $"container={replayRunnerContainer}", StringComparison.Ordinal);
        StringAssert.Contains(diagnostics, $"{replayRunnerContainer} startup", StringComparison.Ordinal);
        StringAssert.Contains(diagnostics, $"{replayRunnerContainer} failure", StringComparison.Ordinal);
        Assert.IsFalse(diagnostics.Contains("runner-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("stop")]
    [DataRow("snapshot")]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_InterruptedPreRecreateBoundaryRestoresOriginalRuntime(string failurePoint)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('d', 64)}";
        var candidateImageId = $"sha256:{new string('e', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.RejectNextStop = failurePoint == "stop";
        if (failurePoint == "snapshot")
        {
            // A store with recovery files but no database cannot be snapshotted, so the snapshot fails after the stop.
            var orphan = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, "raw/.fleet/fleet-status.db") + "-wal";
            Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
            await File.WriteAllTextAsync(orphan, "orphaned log");
        }
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, manifest.Image.ImageId);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(InstallationStatus.Failed, retained!.Status);
        Assert.IsFalse(retained.MutationStarted);
    }

    [TestMethod]
    [DataRow("cameraagent-compose-v1")]
    [DataRow("cameraagent-compose-v3")]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ExecuteAsync_NonCanonicalComposeIsRejectedBeforeDockerOrJournalMutation(string composeVersion)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.ManifestPath, fixture.Manifest with { ComposeTemplateVersion = composeVersion },
            DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.ResultPath, result with { ComposeTemplateVersion = composeVersion },
            DeploymentJsonContext.Default.InstallationResult, CancellationToken.None);
        var retainedManifest = await File.ReadAllTextAsync(fixture.Paths.ManifestPath);
        var retainedResult = await File.ReadAllTextAsync(fixture.Paths.ResultPath);

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(operation: null), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.AreEqual(0, fixture.Runner.InvocationCount);
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
        Assert.AreEqual(retainedManifest, await File.ReadAllTextAsync(fixture.Paths.ManifestPath));
        Assert.AreEqual(retainedResult, await File.ReadAllTextAsync(fixture.Paths.ResultPath));
    }

    [TestMethod]
    [DataRow("cameraagent-compose-v1")]
    [DataRow("cameraagent-compose-v4")]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ExecuteAsync_UnsupportedPreviousComposePropertyIsRejectedBeforeDockerOrJournalMutation(
        string previousComposeVersion)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var json = await File.ReadAllTextAsync(fixture.Paths.ManifestPath);
        await File.WriteAllTextAsync(
            fixture.Paths.ManifestPath,
            json.Replace("{\n", $"{{\n  \"previousComposeTemplateVersion\": \"{previousComposeVersion}\",\n", StringComparison.Ordinal));

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(operation: null), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.AreEqual(0, fixture.Runner.InvocationCount);
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task StatusAsync_CurrentPreviousComposePropertyIsAcceptedWithoutCompatibilityBranch()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var json = await File.ReadAllTextAsync(fixture.Paths.ManifestPath);
        await File.WriteAllTextAsync(
            fixture.Paths.ManifestPath,
            json.Replace("{\n", "{\n  \"previousComposeTemplateVersion\": \"cameraagent-compose-v2\",\n", StringComparison.Ordinal));

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(operation: null), fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("status", result.Outcome);
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RollbackAsync_CurrentComposeRestoresPreviousImageAndExactCompose()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var originalCompose = await File.ReadAllTextAsync(Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml"));
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('a', 64)}";
        var candidateImageId = $"sha256:{new string('b', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var lifecycle = new FakeLifecycleClient();

        await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                ImageReference = candidateReference,
                NoDownload = true,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);

        var upgraded = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual(ComposeDeployment.TemplateVersion, upgraded.ComposeTemplateVersion);
        Assert.AreEqual(fixture.Manifest.Image, upgraded.PreviousImage);
        Assert.AreEqual(CameraAgentContainerResources.Default, upgraded.Resources);
        var retainedRollbackCompose = Path.Combine(fixture.Paths.DeploymentStateRoot, "rollback", "previous-compose.yml");
        await File.WriteAllTextAsync(retainedRollbackCompose, "services: {}\nnetworks:\n  foreign: {}\n");

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback),
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.AreEqual(candidateImageId, fixture.Runner.ActiveImageId);
        // The rollback was refused before any mutation, so its journal is terminal and the repaired rollback runs
        // as an ordinary command rather than a --resume.
        var refused = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationKind.Rollback, refused!.Kind);
        Assert.AreEqual(CameraAgentLifecycleManager.RefusedFailureCode, refused.FailureCode);
        Assert.IsFalse(refused.MutationStarted);
        await File.WriteAllTextAsync(retainedRollbackCompose, originalCompose);

        var rollback = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback),
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual(fixture.Manifest.Image.ImageId, rollback.Image!.ImageId);
        var rolledBack = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual(ComposeDeployment.TemplateVersion, rolledBack.ComposeTemplateVersion);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        // The previous image runs its exact retained Compose model with the limits the instance records: the upgrade
        // moved this instance, recorded without limits, to the defaults, and rolling back keeps them.
        Assert.AreEqual(CameraAgentContainerResources.Default, rolledBack.Resources);
        Assert.AreEqual(
            originalCompose.Replace("    cpus: \"1\"\n    mem_limit: 1G\n", "    cpus: \"2\"\n    mem_limit: 4G\n", StringComparison.Ordinal),
            await File.ReadAllTextAsync(Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml")));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreOnly_HealthyOriginalResumesWithoutBackupOrContainerMutationAndIsTerminalIdempotent()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, recoveryState: true);
        var request = await fixture.PrepareRestoreAsync();
        var lifecycle = new FakeLifecycleClient();
        var manifestBefore = await File.ReadAllBytesAsync(fixture.Paths.ManifestPath);
        var resultBefore = await File.ReadAllBytesAsync(fixture.Paths.ResultPath);
        var rollbackPath = Path.Combine(fixture.Paths.DeploymentStateRoot, "rollback", "previous.env");
        var rollbackBefore = await File.ReadAllBytesAsync(rollbackPath);
        var partial = Path.Combine(fixture.Paths.OperationsRoot, "partial.tar.gz");
        await File.WriteAllTextAsync(partial, "not a valid backup");

        var result = await ExecuteRestoreAsync(fixture, request, lifecycle);
        var journal = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        var journalBytes = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        var repeated = await ExecuteRestoreAsync(fixture, request, lifecycle);

        Assert.AreEqual("restored-previous-healthy-admission-resumed", result.Outcome);
        Assert.AreEqual(result.Outcome, repeated.Outcome);
        Assert.AreEqual(request.RecoveryOperationId, result.OperationId);
        Assert.AreEqual(fixture.Manifest.Image, result.Image);
        Assert.AreEqual(fixture.Manifest.PreviousImage, result.PreviousImage);
        Assert.IsTrue(result.Running);
        Assert.IsTrue(result.Healthy);
        Assert.AreEqual(LifecycleOperationPhase.Restored, journal!.Phase);
        Assert.AreEqual(InstallationStatus.Failed, journal.Status);
        Assert.IsFalse(journal.MutationStarted);
        Assert.AreEqual("lifecycle-failed", journal.FailureCode);
        Assert.AreEqual("Original backup deadline expired.", journal.FailureMessage);
        Assert.AreEqual("owner-ready", journal.ExpectedOwnerBootstrapState);
        Assert.AreEqual("Running", journal.PostMutationContinuity!.CaptureState);
        Assert.AreEqual(3L, journal.PostMutationContinuity.CaptureVersion);
        Assert.AreEqual(1, lifecycle.RecoveryResumeCalls);
        Assert.AreEqual(1, lifecycle.RecoveryCommands.Count);
        CollectionAssert.AreEqual(journalBytes, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        CollectionAssert.AreEqual(manifestBefore, await File.ReadAllBytesAsync(fixture.Paths.ManifestPath));
        CollectionAssert.AreEqual(resultBefore, await File.ReadAllBytesAsync(fixture.Paths.ResultPath));
        CollectionAssert.AreEqual(rollbackBefore, await File.ReadAllBytesAsync(rollbackPath));
        Assert.AreEqual("not a valid backup", await File.ReadAllTextAsync(partial));
        AssertNoRecoveryContainerMutation(fixture, lifecycle);
        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.BeginAsync(
            request with { RestoreOnly = false, RecoveryOperationId = null }, fixture.Paths, LifecycleOperationKind.Upgrade,
            fixture.Manifest, CancellationToken.None));
        var next = await CameraAgentLifecycleManager.BeginAsync(fixture.Request(LifecycleOperationKind.Uninstall),
            fixture.Paths, LifecycleOperationKind.Uninstall, fixture.Manifest, CancellationToken.None);
        Assert.AreNotEqual(journal.OperationId, next.OperationId);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreOnly_LostAcknowledgementReplaysSameVersionFencedCommand()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, recoveryState: true);
        var request = await fixture.PrepareRestoreAsync();
        var lifecycle = new FakeLifecycleClient { LoseRecoveryAcknowledgement = true };
        await Assert.ThrowsExactlyAsync<InstallerException>(() => ExecuteRestoreAsync(fixture, request, lifecycle));
        var interrupted = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.IsTrue(interrupted!.MutationStarted);
        Assert.AreEqual(LifecycleOperationPhase.Restoring, interrupted.Phase);
        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.BeginAsync(
            request with { RestoreOnly = false, RecoveryOperationId = null }, fixture.Paths, LifecycleOperationKind.Upgrade,
            fixture.Manifest, CancellationToken.None));

        var result = await ExecuteRestoreAsync(fixture, request, lifecycle);

        Assert.AreEqual("restored-previous-healthy-admission-resumed", result.Outcome);
        Assert.AreEqual(2, lifecycle.RecoveryResumeCalls);
        Assert.AreEqual(1, lifecycle.RecoveryCommands.Count);
        Assert.AreEqual(interrupted.RestoreResumeCommandId, lifecycle.RecoveryCommands.Single());
        AssertNoRecoveryContainerMutation(fixture, lifecycle);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreOnly_LegacyInterruptedFailureAndAbsentRollbackRemainTruthful()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, recoveryState: true);
        var request = await fixture.PrepareRestoreAsync();
        var operation = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        var root = Path.Combine(fixture.Paths.OperationsRoot, "lifecycle", operation.OperationId.ToString("D"));
        foreach (var (retained, current) in new[] { ("prior-rollback.env", "previous.env"), ("prior-rollback-compose.yml", "previous-compose.yml") })
        {
            File.Delete(Path.Combine(root, retained));
            File.Delete(Path.Combine(fixture.Paths.DeploymentStateRoot, "rollback", current));
            SafeFileSystem.WriteTextAtomic(Path.Combine(root, retained + ".absent"), string.Empty);
        }
        await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with { FailureCode = null, FailureMessage = null }, CancellationToken.None);
        var lifecycle = new FakeLifecycleClient();
        await ExecuteRestoreAsync(fixture, request, lifecycle);
        var restored = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual("upgrade-interrupted", restored!.FailureCode);
        StringAssert.Contains(restored.FailureMessage, "failure detail was not retained", StringComparison.Ordinal);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Paths.DeploymentStateRoot, "rollback", "previous.env")));
        AssertNoRecoveryContainerMutation(fixture, lifecycle);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreOnly_TerminalRepeatDoesNotOverrideLaterOperatorPause()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, recoveryState: true);
        var request = await fixture.PrepareRestoreAsync();
        var lifecycle = new FakeLifecycleClient();
        await ExecuteRestoreAsync(fixture, request, lifecycle);
        lifecycle.RecoveryContinuity = lifecycle.RecoveryContinuity with { CaptureState = "Paused", CaptureVersion = 5 };
        var before = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        var result = await ExecuteRestoreAsync(fixture, request, lifecycle);
        Assert.AreEqual("restored-previous-healthy-resume-superseded-current-admission-preserved", result.Outcome);
        Assert.AreEqual(1, lifecycle.RecoveryResumeCalls);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        AssertNoRecoveryContainerMutation(fixture, lifecycle);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreOnly_LostAcknowledgementThenOperatorPauseSettlesWithoutReopeningAdmission(bool restart)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, recoveryState: true);
        var request = await fixture.PrepareRestoreAsync();
        var lifecycle = new FakeLifecycleClient { LoseRecoveryAcknowledgement = true };
        await Assert.ThrowsExactlyAsync<InstallerException>(() => ExecuteRestoreAsync(fixture, request, lifecycle));
        lifecycle.RecoveryContinuity = lifecycle.RecoveryContinuity with { CaptureState = "Paused", CaptureVersion = 5 };
        if (restart)
        {
            var restarted = new FakeLifecycleClient { RecoveryContinuity = lifecycle.RecoveryContinuity };
            restarted.RecoveryCommands.UnionWith(lifecycle.RecoveryCommands);
            lifecycle = restarted;
        }
        var commandsBefore = lifecycle.RecoveryCommands.Count;
        var result = await ExecuteRestoreAsync(fixture, request, lifecycle);
        Assert.AreEqual("restored-previous-healthy-resume-superseded-current-admission-preserved", result.Outcome);
        Assert.AreEqual("Paused", lifecycle.RecoveryContinuity.CaptureState);
        Assert.AreEqual(5L, lifecycle.RecoveryContinuity.CaptureVersion);
        Assert.AreEqual(commandsBefore, lifecycle.RecoveryCommands.Count);
        var operation = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationPhase.Restored, operation!.Phase);
        Assert.AreEqual(InstallationStatus.Failed, operation.Status);
        Assert.IsFalse(operation.MutationStarted);
        Assert.AreEqual("Running", operation.RestoreResumeReceipt!.State);
        Assert.AreEqual(3L, operation.RestoreResumeReceipt.Version);
        Assert.IsTrue(operation.RestoreResumeReceipt.Replayed);
        Assert.AreEqual("Paused", operation.PostMutationContinuity!.CaptureState);
        Assert.AreEqual(5L, operation.PostMutationContinuity.CaptureVersion);
        var calls = lifecycle.RecoveryResumeCalls;
        await ExecuteRestoreAsync(fixture, request, lifecycle);
        Assert.AreEqual(calls, lifecycle.RecoveryResumeCalls);
        lifecycle.RecoveryContinuity = lifecycle.RecoveryContinuity with { CaptureState = "Running", CaptureVersion = 3 };
        await Assert.ThrowsExactlyAsync<InstallerException>(() => ExecuteRestoreAsync(fixture, request, lifecycle));
        Assert.AreEqual(calls, lifecycle.RecoveryResumeCalls);
        var next = await CameraAgentLifecycleManager.BeginAsync(fixture.Request(LifecycleOperationKind.Uninstall),
            fixture.Paths, LifecycleOperationKind.Uninstall, fixture.Manifest, CancellationToken.None);
        Assert.AreNotEqual(operation.OperationId, next.OperationId);
        AssertNoRecoveryContainerMutation(fixture, lifecycle);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreOnly_FutureResumeReceiptRemainsResumableUntilConsistentReplay(bool laterOperatorPause)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, recoveryState: true);
        var request = await fixture.PrepareRestoreAsync();
        var lifecycle = new FakeLifecycleClient { RecoveryReceiptClockOffset = TimeSpan.FromHours(1) };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => ExecuteRestoreAsync(fixture, request, lifecycle));

        var interrupted = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationPhase.Restoring, interrupted!.Phase);
        Assert.AreEqual(InstallationStatus.Running, interrupted.Status);
        Assert.IsTrue(interrupted.MutationStarted);
        Assert.IsNull(interrupted.PostMutationContinuity);
        Assert.IsNull(interrupted.RestoreResumeReceipt);
        Assert.AreEqual("Running", lifecycle.RecoveryContinuity.CaptureState);
        Assert.AreEqual(3L, lifecycle.RecoveryContinuity.CaptureVersion);
        var commandId = interrupted.RestoreResumeCommandId;

        if (laterOperatorPause)
            lifecycle.RecoveryContinuity = lifecycle.RecoveryContinuity with { CaptureState = "Paused", CaptureVersion = 5 };
        await Assert.ThrowsExactlyAsync<InstallerException>(() => ExecuteRestoreAsync(fixture, request, lifecycle));
        var repeated = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationPhase.Restoring, repeated!.Phase);
        Assert.IsTrue(repeated.MutationStarted);
        Assert.AreEqual(commandId, repeated.RestoreResumeCommandId);
        Assert.IsNull(repeated.RestoreResumeReceipt);
        Assert.AreEqual(1, lifecycle.RecoveryCommands.Count);

        lifecycle.RecoveryReceiptClockOffset = TimeSpan.Zero;
        var result = await ExecuteRestoreAsync(fixture, request, lifecycle);
        var restored = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(laterOperatorPause
            ? "restored-previous-healthy-resume-superseded-current-admission-preserved"
            : "restored-previous-healthy-admission-resumed", result.Outcome);
        Assert.AreEqual(LifecycleOperationPhase.Restored, restored!.Phase);
        Assert.AreEqual(InstallationStatus.Failed, restored.Status);
        Assert.IsFalse(restored.MutationStarted);
        Assert.AreEqual(commandId, restored.RestoreResumeCommandId);
        Assert.IsTrue(restored.RestoreResumeReceipt!.Replayed);
        Assert.IsTrue(restored.RestoreResumeReceipt.CompletedUtc <= restored.PostMutationContinuity!.RecordedUtc);
        Assert.AreEqual(laterOperatorPause ? "Paused" : "Running", restored.PostMutationContinuity.CaptureState);
        Assert.AreEqual(laterOperatorPause ? 5L : 3L, restored.PostMutationContinuity.CaptureVersion);
        Assert.AreEqual(3, lifecycle.RecoveryResumeCalls);
        var before = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        await ExecuteRestoreAsync(fixture, request, lifecycle);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        Assert.AreEqual(3, lifecycle.RecoveryResumeCalls);
        Assert.AreEqual(1, lifecycle.RecoveryCommands.Count);
        AssertNoRecoveryContainerMutation(fixture, lifecycle);
        var next = await CameraAgentLifecycleManager.BeginAsync(fixture.Request(LifecycleOperationKind.Uninstall),
            fixture.Paths, LifecycleOperationKind.Uninstall, fixture.Manifest, CancellationToken.None);
        Assert.AreNotEqual(restored.OperationId, next.OperationId);
    }

    [TestMethod]
    [DataRow("reference")]
    [DataRow("id")]
    [DataRow("archive")]
    [DataRow("architecture")]
    [DataRow("source")]
    [DataRow("repository-id")]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreOnly_CanonicalCandidateFieldsMustCorrelateWithRequest(string field)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, recoveryState: true);
        var request = await fixture.PrepareRestoreAsync();
        var operation = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        if (field == "repository-id")
        {
            request = request with { ImageReference = $"example/camera@sha256:{new string('5', 64)}" };
            operation = operation with { RequestSha256 = request.ComputeRequestSha256(), CandidateImage = operation.CandidateImage! with { ImmutableReference = request.ImageReference } };
        }
        var candidate = operation.CandidateImage!;
        candidate = field switch
        {
            "reference" => candidate with { ImmutableReference = $"sha256:{new string('6', 64)}" },
            "id" or "repository-id" => candidate with { ImageId = $"sha256:{new string('6', 64)}" },
            "archive" => candidate with { ArchiveSha256 = new string('6', 64) },
            "architecture" => candidate with { Architecture = candidate.Architecture == "amd64" ? "arm64" : "amd64" },
            "source" => candidate with { Source = "archive" },
            _ => throw new InvalidOperationException()
        };
        await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with { CandidateImage = candidate }, CancellationToken.None);
        var before = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        var lifecycle = new FakeLifecycleClient();
        await Assert.ThrowsExactlyAsync<InstallerException>(() => ExecuteRestoreAsync(fixture, request, lifecycle));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        Assert.AreEqual(0, lifecycle.RecoveryResumeCalls);
        AssertNoRecoveryContainerMutation(fixture, lifecycle);
    }

    [TestMethod]
    [DataRow("paused-post")]
    [DataRow("post-version")]
    [DataRow("post-sequence")]
    [DataRow("negative-count")]
    [DataRow("negative-pre")]
    [DataRow("negative-replay")]
    [DataRow("uninitialized")]
    [DataRow("receipt-missing")]
    [DataRow("receipt-version")]
    [DataRow("receipt-unchanged")]
    [DataRow("receipt-state")]
    [DataRow("post-time")]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreOnly_TerminalEvidenceMustProveExecutedResumeAndCurrentBoundary(string field)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, recoveryState: true);
        var request = await fixture.PrepareRestoreAsync();
        var lifecycle = new FakeLifecycleClient();
        await ExecuteRestoreAsync(fixture, request, lifecycle);
        var operation = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        operation = field switch
        {
            "paused-post" => operation with { PostMutationContinuity = operation.PreMutationContinuity },
            "post-version" => operation with { PostMutationContinuity = operation.PostMutationContinuity! with { CaptureVersion = 2 } },
            "post-sequence" => operation with { PostMutationContinuity = operation.PostMutationContinuity! with { CaptureSequence = 1811 } },
            "negative-count" => operation with { PostMutationContinuity = operation.PostMutationContinuity! with { OutboxLeased = -1 } },
            "negative-pre" => operation with { PreMutationContinuity = operation.PreMutationContinuity! with { CaptureVersion = -1 } },
            "negative-replay" => operation with { PreMutationContinuity = operation.PreMutationContinuity! with { ReplayLeased = -1 } },
            "uninitialized" => operation with { PostMutationContinuity = operation.PostMutationContinuity! with { CaptureInitialized = false } },
            "receipt-missing" => operation with { RestoreResumeReceipt = null },
            "receipt-version" => operation with { RestoreResumeReceipt = operation.RestoreResumeReceipt! with { Version = 5 } },
            "receipt-unchanged" => operation with { RestoreResumeReceipt = operation.RestoreResumeReceipt! with { Changed = false } },
            "receipt-state" => operation with { RestoreResumeReceipt = operation.RestoreResumeReceipt! with { State = "Paused" } },
            "post-time" => operation with { PostMutationContinuity = operation.PostMutationContinuity! with { RecordedUtc = default } },
            _ => throw new InvalidOperationException()
        };
        var validTerminal = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation, CancellationToken.None));
        CollectionAssert.AreEqual(validTerminal, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        await SafeFileSystem.WriteJsonAtomicAsync(fixture.Paths.LifecycleStatePath, operation,
            DeploymentJsonContext.Default.LifecycleOperationState, CancellationToken.None);
        var before = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        await Assert.ThrowsExactlyAsync<InstallerException>(() => ExecuteRestoreAsync(fixture, request, lifecycle));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        Assert.AreEqual(1, lifecycle.RecoveryResumeCalls);
        AssertNoRecoveryContainerMutation(fixture, lifecycle);
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("missing")]
    [DataRow("malformed")]
    [DataRow("configured")]
    [DataRow("bound")]
    [DataRow("schema")]
    [DataRow("state")]
    [DataRow("symlink")]
    [DataRow("mode")]
    [DataRow("oversized")]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreOnly_ProtectedApplicationBindingMustMatchBothOriginalIdentities(string fault)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, recoveryState: true);
        var request = await fixture.PrepareRestoreAsync();
        var path = fixture.Paths.ApplicationIdentityPath;
        var binding = new ApplicationIdentityBinding(1, "bound", fixture.ApplicationIdentity, fixture.ApplicationIdentity);
        if (fault is "configured" or "bound" or "schema" or "state")
        {
            binding = fault switch
            {
                "configured" => binding with { ConfiguredIdentity = Guid.NewGuid() },
                "bound" => binding with { BoundIdentity = Guid.NewGuid() },
                "schema" => binding with { SchemaVersion = 2 },
                _ => binding with { State = "unbound" }
            };
            await SafeFileSystem.WriteJsonAtomicAsync(path, binding, DeploymentJsonContext.Default.ApplicationIdentityBinding, CancellationToken.None);
        }
        else if (fault == "missing") File.Delete(path);
        else if (fault == "symlink")
        {
            var target = Path.Combine(fixture.Root, "other-identity.json");
            File.Move(path, target);
            File.CreateSymbolicLink(path, target);
        }
        else if (fault == "mode") File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        else SafeFileSystem.WriteTextAtomic(path, fault == "empty" ? "{}" : fault == "oversized" ? new string('x', 1024 * 1024 + 1) : "{malformed");
        var before = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        var lifecycle = new FakeLifecycleClient();
        await Assert.ThrowsExactlyAsync<InstallerException>(() => ExecuteRestoreAsync(fixture, request, lifecycle));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        Assert.AreEqual(0, lifecycle.RecoveryResumeCalls);
        AssertNoRecoveryContainerMutation(fixture, lifecycle);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreOnly_SchemaOneJournalWithoutNewFieldsAllowsOwnerPasswordProgression()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, recoveryState: true);
        var request = await fixture.PrepareRestoreAsync();
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath))!.AsObject();
        json.Remove("restoreResumeCommandId");
        json.Remove("restoreResumeReceipt");
        json.Remove("originalOwnerBootstrapState");
        json["preMutationContinuity"]!.AsObject().Remove("captureInitialized");
        SafeFileSystem.WriteTextAtomic(fixture.Paths.LifecycleStatePath, json.ToJsonString());
        var lifecycle = new FakeLifecycleClient();
        var result = await ExecuteRestoreAsync(fixture, request, lifecycle);
        Assert.AreEqual("restored-previous-healthy-admission-resumed", result.Outcome);
        var operation = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual("owner-ready", operation!.ExpectedOwnerBootstrapState);
        Assert.IsNotNull(operation.RestoreResumeReceipt);
        AssertNoRecoveryContainerMutation(fixture, lifecycle);
    }

    [TestMethod]
    [DataRow("candidate")]
    [DataRow("unhealthy")]
    [DataRow("absent")]
    [DataRow("ownership")]
    [DataRow("ports")]
    [DataRow("capabilities")]
    [DataRow("owner")]
    [DataRow("expired-verification")]
    [DataRow("expired-lifecycle")]
    [DataRow("token-mirror")]
    [DataRow("verification-token")]
    [DataRow("daemon")]
    [DataRow("operation")]
    [DataRow("request")]
    [DataRow("foreign-pause")]
    [DataRow("unowned-running")]
    [DataRow("schema-advanced")]
    [DataRow("manifest")]
    [DataRow("result")]
    [DataRow("compose")]
    [DataRow("environment")]
    [DataRow("compose-hash")]
    [DataRow("configuration")]
    [DataRow("rollback-history")]
    [DataRow("linked-snapshot")]
    [DataRow("oversized-snapshot")]
    [DataRow("committed")]
    [DataRow("completed")]
    [DataRow("partial-commit")]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreOnly_UnsafeBoundaryRefusesWithoutMutation(string fault)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, recoveryState: true);
        var request = await fixture.PrepareRestoreAsync();
        var operationRoot = Path.Combine(fixture.Paths.OperationsRoot, "lifecycle", request.RecoveryOperationId!.Value.ToString("D"));
        var lifecycle = new FakeLifecycleClient();
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { CurrentOwnerBootstrapState = "owner-ready" };
        var operation = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        switch (fault)
        {
            case "candidate": fixture.Runner.UseCandidateRuntime(); break;
            case "unhealthy": fixture.Runner.Unhealthy = true; break;
            case "absent": fixture.Runner.RemoveRuntime(); break;
            case "ownership": fixture.Runner.OmitOwnershipLabel = true; break;
            case "ports": fixture.Runner.WrongPort = true; break;
            case "capabilities": fixture.Runner.RetainCapabilities = true; break;
            case "owner": owner.CurrentOwnerBootstrapState = "owner-password-change-required"; break;
            case "expired-verification": owner.RejectNextVerification = true; break;
            case "expired-lifecycle": lifecycle.RejectRecoveryRead = true; break;
            case "token-mirror": SafeFileSystem.WriteTextAtomic(Path.Combine(fixture.Paths.ConfigRoot, "secrets", "LifecycleControl__Token"), "changed"); break;
            case "verification-token": SafeFileSystem.WriteTextAtomic(Path.Combine(fixture.Paths.ConfigRoot, "installation-verification", "token"), "changed"); break;
            case "daemon": fixture.Runner.DaemonDriftAtInfoCall = 1; break;
            case "operation": request = request with { RecoveryOperationId = Guid.NewGuid() }; break;
            case "request": request = request with { NoDownload = false }; break;
            case "foreign-pause": lifecycle.RecoveryContinuity = lifecycle.RecoveryContinuity with { CaptureVersion = 4 }; break;
            case "unowned-running": lifecycle.RecoveryContinuity = lifecycle.RecoveryContinuity with { CaptureState = "Running", CaptureVersion = 3 }; break;
            case "schema-advanced":
                await using (var connection = new SqliteConnection($"Data Source={CameraAgentStateLayout.RawIngressDatabasePath(fixture.Paths.StateRoot)}"))
                {
                    await connection.OpenAsync();
                    using var command = connection.CreateCommand();
                    command.CommandText = "PRAGMA user_version=14;";
                    await command.ExecuteNonQueryAsync();
                }
                break;
            case "manifest":
                await SafeFileSystem.WriteJsonAtomicAsync(Path.Combine(operationRoot, "previous-instance-manifest.json"),
                    fixture.Manifest with { PreviousComposeModelSha256 = new string('0', 64) }, DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
                break;
            case "result":
                var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
                await SafeFileSystem.WriteJsonAtomicAsync(Path.Combine(operationRoot, "previous-installation-result.json"),
                    result with { PasswordFile = "/foreign/credential" }, DeploymentJsonContext.Default.InstallationResult, CancellationToken.None);
                break;
            case "compose": SafeFileSystem.WriteTextAtomic(Path.Combine(operationRoot, "previous-compose.yml"), "services: changed"); break;
            case "environment": SafeFileSystem.WriteTextAtomic(Path.Combine(operationRoot, "previous.env"), "CAMERAAGENT_IMAGE=changed"); break;
            case "compose-hash":
                SafeFileSystem.WriteTextAtomic(Path.Combine(operationRoot, "previous-compose.yml"), "services: changed");
                SafeFileSystem.WriteTextAtomic(Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml"), "services: changed");
                break;
            case "configuration": SafeFileSystem.WriteTextAtomic(Path.Combine(fixture.Paths.ConfigRoot, "camera-module.json"), "{\"changed\":true}"); break;
            case "rollback-history": SafeFileSystem.WriteTextAtomic(Path.Combine(fixture.Paths.DeploymentStateRoot, "rollback", "previous.env"), "changed"); break;
            case "linked-snapshot":
                var path = Path.Combine(operationRoot, "previous.env");
                File.Delete(path);
                File.CreateSymbolicLink(path, Path.Combine(fixture.Paths.ConfigRoot, "compose", "instance.env"));
                break;
            case "oversized-snapshot": SafeFileSystem.WriteTextAtomic(Path.Combine(operationRoot, "previous.env"), new string('x', 1024 * 1024 + 1)); break;
            case "committed":
            case "completed":
                await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with
                {
                    Phase = fault == "committed" ? LifecycleOperationPhase.Committed : LifecycleOperationPhase.Completed,
                    Status = fault == "committed" ? InstallationStatus.Running : InstallationStatus.Completed,
                    MutationStarted = fault == "committed",
                    PostMutationContinuity = operation.PreMutationContinuity
                }, CancellationToken.None);
                break;
            case "partial-commit":
                await SafeFileSystem.WriteJsonAtomicAsync(fixture.Paths.ManifestPath, fixture.Manifest with
                {
                    LastLifecycleOperationId = operation.OperationId
                }, DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
                break;
            default: Assert.Fail("Unknown fault"); break;
        }
        var journalBefore = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        var manifestBefore = await File.ReadAllBytesAsync(fixture.Paths.ManifestPath);

        await Assert.ThrowsExactlyAsync<InstallerException>(() => ExecuteRestoreAsync(fixture, request, lifecycle, owner));

        CollectionAssert.AreEqual(journalBefore, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        CollectionAssert.AreEqual(manifestBefore, await File.ReadAllBytesAsync(fixture.Paths.ManifestPath));
        Assert.AreEqual(0, lifecycle.RecoveryResumeCalls);
        AssertNoRecoveryContainerMutation(fixture, lifecycle);
    }

    private static Task<LifecycleResult> ExecuteRestoreAsync(
        LifecycleFixture fixture, LifecycleRequest request, FakeLifecycleClient lifecycle, FakeOwnerClient? owner = null)
        => CameraAgentLifecycleManager.ExecuteAsync(request, fixture.Runner, _ => lifecycle,
            _ => owner ?? new FakeOwnerClient(fixture.ApplicationIdentity) { CurrentOwnerBootstrapState = "owner-ready" },
            fixture.Uid, fixture.Gid, CancellationToken.None);

    private static void AssertNoRecoveryContainerMutation(LifecycleFixture fixture, FakeLifecycleClient lifecycle)
    {
        Assert.AreEqual(0, fixture.Runner.BackupCount);
        Assert.AreEqual(0, fixture.Runner.ComposeUpCount);
        Assert.AreEqual(0, fixture.Runner.ComposeStopCount);
        Assert.AreEqual(0, fixture.Runner.ComposeRestartCount);
        Assert.AreEqual(0, fixture.Runner.ComposeDownCount);
        Assert.AreEqual(0, fixture.Runner.ImagePreparationCount);
        Assert.AreEqual(0, lifecycle.PauseCount);
        Assert.AreEqual(0, lifecycle.ConfirmCount);
        Assert.AreEqual(0, fixture.Runner.LoggedContainers.Count);
    }

    internal sealed class LifecycleFixture : IDisposable
    {
        private LifecycleFixture(
            string root,
            Guid instanceId,
            Guid applicationIdentity,
            InstallationPaths paths,
            InstanceManifest manifest,
            FakeRunner runner,
            uint uid,
            uint gid)
        {
            Root = root;
            InstanceId = instanceId;
            ApplicationIdentity = applicationIdentity;
            Paths = paths;
            Manifest = manifest;
            Runner = runner;
            Uid = uid;
            Gid = gid;
        }

        public string Root { get; }
        public Guid InstanceId { get; }
        public Guid ApplicationIdentity { get; }
        public InstallationPaths Paths { get; }
        public InstanceManifest Manifest { get; }
        public FakeRunner Runner { get; }
        public uint Uid { get; }
        public uint Gid { get; }

        public static async Task<LifecycleFixture> CreateAsync(
            InstanceLifecycleCondition condition,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile replayProfile =
                HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.InProcess,
            bool seedCatalogSelection = true,
            string? daemonArchitecture = null,
            bool recoveryState = false,
            string catalogId = HygV42.CatalogId,
            Func<ImageInstallationIdentity, ImageInstallationIdentity>? installedImage = null)
        {
            // Every approved catalog is a valid selection; the fixture records the pinned identity of the requested
            // one, and a non-default catalog needs an image declaring the multi-catalog contract.
            var specification = ProductionCatalog.Get(catalogId);
            var packageVersion = specification.PackageVersionPrefix + "1";
            // By default the fixture records the architecture of the process running the tests, as a real
            // installation on this machine would, so host-relative tests stay deterministic on amd64 and arm64
            // runners alike; tests that prove daemon-over-process selection pass a different value explicitly.
            daemonArchitecture ??= ReleaseAcquirer.HostImageArchitecture();
            var root = Path.Combine(Path.GetTempPath(), $"hvo-lifecycle-{Guid.NewGuid():N}");
            var instanceId = Guid.NewGuid();
            var paths = InstallationPaths.Create(root, instanceId, catalogId);
            foreach (var directory in new[]
            {
                paths.ProductRoot, Path.Combine(paths.ProductRoot, "cameraagents"), paths.InstanceRoot,
                paths.ConfigRoot, paths.StateRoot, paths.DeploymentStateRoot,
                Path.Combine(paths.ConfigRoot, "compose"), paths.OperationsRoot
            }) Directory.CreateDirectory(directory);
            // The state preflight reads the selected catalog manifest before any lifecycle mutation, so the fixture
            // publishes the same manifest-version and catalog-identity pointer a real installation exposes. Tests
            // that install a real bundle into this root opt out because the adopted root needs its lineage binding.
            if (seedCatalogSelection)
            {
                var catalogVersionRoot = Path.Combine(paths.CatalogRoot, "versions", packageVersion);
                Directory.CreateDirectory(catalogVersionRoot);
                await File.WriteAllTextAsync(
                    Path.Combine(catalogVersionRoot, "manifest.json"),
                    "{\"manifestVersion\":2,\"catalog\":{\"id\":\"" + catalogId + "\"}}",
                    CancellationToken.None);
                Directory.CreateSymbolicLink(
                    Path.Combine(paths.CatalogRoot, "current"), $"versions/{packageVersion}");
            }
            var uid = NativeLinux.getuid();
            var gid = NativeLinux.getgid();
            // The recorded daemon architecture and the installed image's architecture agree, as a real installation
            // guarantees; a test that records an architecture other than the process's proves that the daemon, not
            // the CLI process, selects the signed platform.
            var daemon = new DockerDaemonIdentity("daemon", "host", daemonArchitecture, "29.7.2");
            var catalog = new CatalogInstallationIdentity(
                catalogId, packageVersion, specification.SchemaVersion, specification.PreprocessingVersion,
                specification.DatabaseSha256, specification.DatabaseLength, specification.RowCount,
                paths.CatalogRoot, new string('a', 64), "local-offline");
            var localRunner = replayProfile == HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner;
            var composeTemplateVersion = localRunner
                ? ComposeDeployment.LocalRunnerTemplateVersion
                : ComposeDeployment.TemplateVersion;
            var image = new ImageInstallationIdentity(
                "registry", $"cameraagent@sha256:{new string('b', 64)}", $"sha256:{new string('c', 64)}",
                daemonArchitecture, null, UpgradeCompatibility: "backward-compatible", SourceRevision: new string('8', 40),
                Component: "CameraAgent", ConfigurationContract: "cameraagent-install-v1",
                CatalogContract: catalogId == HygV42.CatalogId ? "hyg-v42-production-p3-s2" : "hvo-approved-catalogs-v1",
                ReplayRunnerContract: localRunner ? "local-replay-runner-v1" : null,
                RawIngressSchema: recoveryState ? "13" : "12",
                IdentityMigration: recoveryState ? "20260827053715_InitialIdentity" : null,
                CatalogManifestVersion: recoveryState ? "2" : null,
                MinimumCompatibleRevision: recoveryState ? new string('7', 40) : null);
            if (installedImage is not null) image = installedImage(image);
            var installationId = Guid.NewGuid();
            // The bound application identity is deliberately distinct from the instance id so that
            // lifecycle verification cannot pass by comparing the wrong identity.
            var applicationIdentity = Guid.NewGuid();
            // The CameraAgent service carries the limit lines every installer writes. The fixture records no limits, as an
            // instance an earlier installer completed does, so it runs the original ones until its next upgrade.
            const string composeModel =
                "services:\n  cameraagent:\n    image: ${CAMERAAGENT_IMAGE}\n    cpus: \"1\"\n    mem_limit: 1G\n    pids_limit: 256\n";
            var manifest = new InstanceManifest(
                1, "HVO.SkyMonitor", "cameraagent-install-v1", DeploymentComponent.CameraAgent,
                instanceId, "Test Camera", applicationIdentity, $"installer-{instanceId:D}", 1,
                new string('d', 64), "owner@example.test", 0, 0, 0, "UTC", installationId, uid, gid,
                paths.ProductRoot, paths.ConfigRoot, paths.StateRoot, composeTemplateVersion,
                new string('e', 64), new string('f', 64), new string('1', 64), "default", "1", "1", "active",
                ComposeDeployment.ComputeSha256("verification-token"), ComposeDeployment.ComputeSha256(composeModel),
                catalog, image, null, daemon, image.UpgradeCompatibility ?? "requires-declared-compatible-migration",
                DateTimeOffset.UtcNow, LifecycleCondition: condition, Port: 5130,
                LifecycleControlTokenSha256: ComposeDeployment.ComputeSha256("lifecycle-token"),
                ReplayProfile: replayProfile);
            if (recoveryState)
            {
                using var configuration = JsonDocument.Parse("{}");
                manifest = manifest with
                {
                    ConfigurationSha256 = Convert.ToHexStringLower(Convert.FromHexString(CaptureContractJson.ComputeCanonicalJsonSha256(configuration.RootElement))),
                    PreviousImage = image with { ImageId = $"sha256:{new string('6', 64)}" },
                    PreviousComposeModelSha256 = manifest.ComposeModelSha256
                };
                SafeFileSystem.WriteTextAtomic(Path.Combine(paths.ConfigRoot, "camera-module.json"), "{}");
                foreach (var directory in ComposeDeployment.WritableStateDirectories(paths.StateRoot))
                    SafeFileSystem.CreateRuntimeDirectory(directory, uid, gid);
                foreach (var databasePath in new[]
                         {
                             CameraAgentStateLayout.RawIngressDatabasePath(paths.StateRoot),
                             CameraAgentStateLayout.IdentityDatabasePath(paths.StateRoot)
                         })
                {
                    SafeFileSystem.CreateOwnerDirectory(Path.GetDirectoryName(databasePath)!);
                    await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
                    await connection.OpenAsync();
                    using var command = connection.CreateCommand();
                    if (databasePath == CameraAgentStateLayout.RawIngressDatabasePath(paths.StateRoot))
                        command.CommandText = "CREATE TABLE sentinel(value); PRAGMA user_version=13;";
                    else
                        command.CommandText = "CREATE TABLE __EFMigrationsHistory(MigrationId TEXT); INSERT INTO __EFMigrationsHistory VALUES('20260827053715_InitialIdentity');";
                    await command.ExecuteNonQueryAsync();
                    File.SetUnixFileMode(databasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            var result = new InstallationResult(
                1, InstallationOutcome.Installed, installationId, instanceId, applicationIdentity, "Test Camera",
                new Uri("http://127.0.0.1:5130"), "owner@example.test", "/tmp/password", paths.ProductRoot,
                paths.InstanceRoot, paths.ConfigRoot, paths.StateRoot, uid, gid, composeTemplateVersion,
                0, 0, 0, "UTC", manifest.ConfigurationSha256, manifest.RigProfileSha256, manifest.ScheduleSha256,
                manifest.RigProfileName, manifest.RigProfileVersion, manifest.ScheduleSchemaVersion,
                manifest.ScheduleState, manifest.ComposeModelSha256, catalog, image, daemon, true, true,
                "owner-password-change-required", DateTimeOffset.UtcNow, replayProfile);
            await SafeFileSystem.WriteJsonAtomicAsync(
                paths.ManifestPath, manifest, DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
            await SafeFileSystem.WriteJsonAtomicAsync(
                paths.ResultPath, result, DeploymentJsonContext.Default.InstallationResult, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(paths.ConfigRoot, "compose", "compose.yml"), composeModel);
            await File.WriteAllTextAsync(
                Path.Combine(paths.ConfigRoot, "compose", "instance.env"),
                $"CAMERAAGENT_IMAGE={image.ImageId}\nHVO_CATALOG_ROOT={paths.CatalogRoot}\n");
            await SafeFileSystem.WriteJsonAtomicAsync(paths.ApplicationIdentityPath,
                new ApplicationIdentityBinding(1, "bound", applicationIdentity, applicationIdentity),
                DeploymentJsonContext.Default.ApplicationIdentityBinding, CancellationToken.None);
            SafeFileSystem.WriteTextAtomic(Path.Combine(paths.ConfigRoot, "installation-verification", "token"), "verification-token");
            SafeFileSystem.WriteTextAtomic(Path.Combine(paths.ConfigRoot, "lifecycle-control", "token"), "lifecycle-token");
            SafeFileSystem.WriteTextAtomic(Path.Combine(paths.ConfigRoot, "secrets", "LifecycleControl__Token"), "lifecycle-token");
            SafeFileSystem.WriteTextAtomic(
                Path.Combine(paths.ConfigRoot, "secrets", "Catalog__RequiredPackageVersion"), catalog.PackageVersion);
            SafeFileSystem.WriteTextAtomic(
                Path.Combine(paths.ConfigRoot, "secrets", "Catalog__RequiredCatalogId"), catalog.CatalogId);
            foreach (var directory in Directory.EnumerateDirectories(paths.InstanceRoot, "*", SearchOption.AllDirectories).Prepend(paths.InstanceRoot))
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new LifecycleFixture(root, instanceId, applicationIdentity, paths, manifest, new FakeRunner(daemon, localRunner) { CatalogContract = image.CatalogContract! }, uid, gid);
        }

        public LifecycleRequest Request(LifecycleOperationKind? operation)
            => new(operation, InstanceId, Root, dryRun: false, resume: false, json: false)
            {
                AllowTestProductRoot = true
            };

        public async Task<LifecycleRequest> PrepareRestoreAsync()
        {
            var request = Request(LifecycleOperationKind.Upgrade) with
            {
                ImageReference = $"sha256:{new string('5', 64)}",
                MigrationBackwardCompatible = true,
                NoDownload = true
            };
            Runner.ConfigureRuntime(Paths, Manifest.Image.ImmutableReference, Manifest.Image.ImageId,
                request.ImageReference, request.ImageReference, Uid, Gid);
            var operation = await CameraAgentLifecycleManager.BeginAsync(request, Paths, LifecycleOperationKind.Upgrade, Manifest, CancellationToken.None);
            var operationRoot = Path.Combine(Paths.OperationsRoot, "lifecycle", operation.OperationId.ToString("D"));
            SafeFileSystem.CreateOwnerDirectory(Path.Combine(Paths.OperationsRoot, "lifecycle"));
            SafeFileSystem.CreateOwnerDirectory(operationRoot);
            foreach (var (source, destination) in new[]
                     {
                         (Paths.ManifestPath, "previous-instance-manifest.json"),
                         (Paths.ResultPath, "previous-installation-result.json"),
                         (Path.Combine(Paths.ConfigRoot, "compose", "compose.yml"), "previous-compose.yml"),
                         (Path.Combine(Paths.ConfigRoot, "compose", "instance.env"), "previous.env")
                     })
            {
                SafeFileSystem.WriteTextAtomic(source, await File.ReadAllTextAsync(source));
                SafeFileSystem.WriteTextAtomic(Path.Combine(operationRoot, destination), await File.ReadAllTextAsync(source));
            }
            SafeFileSystem.WriteTextAtomic(Path.Combine(operationRoot, "prior-rollback.env"), "prior rollback environment");
            SafeFileSystem.WriteTextAtomic(
                Path.Combine(operationRoot, "candidate.env"),
                CameraAgentLifecycleManager.ReplaceEnvironmentValue(
                    await File.ReadAllTextAsync(Path.Combine(operationRoot, "previous.env")), "CAMERAAGENT_IMAGE", request.ImageReference));
            SafeFileSystem.WriteTextAtomic(Path.Combine(operationRoot, "prior-rollback-compose.yml"), "prior rollback compose");
            SafeFileSystem.WriteTextAtomic(Path.Combine(Paths.DeploymentStateRoot, "rollback", "previous.env"), "prior rollback environment");
            SafeFileSystem.WriteTextAtomic(Path.Combine(Paths.DeploymentStateRoot, "rollback", "previous-compose.yml"), "prior rollback compose");
            await CameraAgentLifecycleManager.RecordAsync(Paths, operation with
            {
                Phase = LifecycleOperationPhase.Restoring,
                MutationStarted = true,
                CandidateImage = Manifest.Image with { ImageId = request.ImageReference, ImmutableReference = request.ImageReference },
                PreMutationContinuity = new LifecycleContinuityBoundary("Paused", 2, 1812, 0, 0, 0, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow),
                ExpectedOwnerBootstrapState = "owner-ready",
                FailureCode = "lifecycle-failed",
                FailureMessage = "Original backup deadline expired."
            }, CancellationToken.None);
            return request with { Resume = true, RestoreOnly = true, RecoveryOperationId = operation.OperationId };
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                SafeFileSystem.MakeTreeOwnerWritable(Root);
                Directory.Delete(Root, true);
            }
        }
    }

    internal sealed class FakeRunner(DockerDaemonIdentity daemon, bool localRunner = false) : IProcessRunner
    {
        // Compose mounts the catalog root the instance environment names, so a cross-lineage selection that rewrites
        // HVO_CATALOG_ROOT is observed as a different read-only catalog mount, exactly as the daemon would report it.
        private static string MountedCatalogRoot(InstallationPaths installationPaths)
        {
            var environment = Path.Combine(installationPaths.ConfigRoot, "compose", "instance.env");
            const string Prefix = "HVO_CATALOG_ROOT=";
            return File.Exists(environment)
                ? File.ReadLines(environment).Where(static line => line.StartsWith(Prefix, StringComparison.Ordinal))
                    .Select(static line => line[Prefix.Length..]).SingleOrDefault() ?? installationPaths.CatalogRoot
                : installationPaths.CatalogRoot;
        }

        private static readonly string[] ReplayRunnerEntrypoint =
            ["/app/replay-runner/HVO.SkyMonitor.CameraAgent.ReplayRunner"];
        private static readonly string[] DroppedCapabilities = ["ALL"];
        private static readonly string[] ReplayRunnerSecurityOptions = ["no-new-privileges:true"];
        private InstallationPaths? paths;
        private string? initialReference;
        private string? initialImageId;
        private string? activeImageId;
        private string? candidateReference;
        private string? candidateImageId;
        private uint uid;
        private uint gid;
        private bool running;

        public string? ActiveImageId => activeImageId;
        public bool Running => running;
        /// <summary>Runs after each Compose <c>up</c>, with the image it started, as that image's own startup would.</summary>
        public Action<string>? OnComposeUp { get; set; }
        /// <summary>What a host restart after a power loss brings back: the last container, running unless it was stopped.</summary>
        public void Reboot(string? image, bool wasRunning)
        {
            activeImageId = image;
            running = image is not null && wasRunning;
        }
        public int InvocationCount { get; private set; }
        public int ComposeDownCount { get; private set; }
        public int ComposeUpCount { get; private set; }
        public int ComposeRestartCount { get; private set; }
        public int ComposeStopCount { get; private set; }
        public int BackupCount { get; private set; }
        public int ImagePreparationCount { get; private set; }
        public bool Unhealthy { get; set; }
        public bool WrongPort { get; set; }
        public bool RetainCapabilities { get; set; }
        public void UseCandidateRuntime() => activeImageId = candidateImageId;
        public void RemoveRuntime() => activeImageId = null;
        public int? DaemonDriftAtInfoCall { get; set; }
        public bool DriftDaemonAfterComposeUp { get; set; }
        public bool RetainOwnedOrphanAfterDown { get; set; }
        public bool OmitOwnershipLabel { get; set; }
        public bool RejectNextStop { get; set; }
        public string CandidateRawIngressSchema { get; set; } = "12";
        /// <summary>Candidate image labels to set, or with a null value to remove, over the defaults below.</summary>
        public Dictionary<string, string?> CandidateLabels { get; } = new(StringComparer.Ordinal);

        /// <summary>The labels the daemon reports for the candidate image now, which a release's archive declares too.</summary>
        public Dictionary<string, string> CandidateImageLabels() => ImageLabels(isCandidate: true);

        private Dictionary<string, string> ImageLabels(bool isCandidate)
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["io.hvo.skymonitor.state-compatibility"] = "cameraagent-state-v2",
                ["io.hvo.skymonitor.minimum-compatible-revision"] = new string('7', 40),
                ["io.hvo.skymonitor.identity-migration"] = "20260827053715_InitialIdentity",
                ["io.hvo.skymonitor.raw-ingress-schema"] = isCandidate ? CandidateRawIngressSchema : "12",
                ["io.hvo.skymonitor.catalog-manifest-version"] = "2",
                ["org.opencontainers.image.revision"] = isCandidate ? new string('9', 40) : new string('8', 40),
                ["io.hvo.skymonitor.component"] = "CameraAgent",
                ["io.hvo.skymonitor.configuration-contract"] = "cameraagent-install-v1",
                ["io.hvo.skymonitor.catalog-contract"] = CatalogContract
            };
            if (localRunner)
            {
                labels["io.hvo.skymonitor.replay-runner-contract"] = "local-replay-runner-v1";
            }
            if (isCandidate)
            {
                foreach (var (name, value) in CandidateLabels)
                {
                    if (value is null) labels.Remove(name);
                    else labels[name] = value;
                }
            }
            return labels;
        }
        public string CatalogContract { get; set; } = "hyg-v42-production-p3-s2";
        public Action? OnCandidateInspect { get; set; }
        public Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>>? Sudo { get; set; }
        public int SudoCount { get; private set; }
        public bool ListOwnedOrphan { get; set; }
        public List<string> Events { get; } = [];
        public List<string> LoggedContainers { get; } = [];

        /// <summary>
        /// The names Docker holds the candidate image by, when a test follows what Docker holds: a load or pull adds it,
        /// an inspect of a name it does not hold fails, and a removal by any name takes it away. The running image is
        /// always held. <see langword="null"/> keeps every inspect successful, as the other flows assume.
        /// </summary>
        public HashSet<string>? CandidateHeld { get; set; }

        /// <summary>Every name an <c>image rm</c> was asked to remove, in order.</summary>
        public List<string> RemovedImages { get; } = [];

        public int LoadCount { get; private set; }

        public int PullCount { get; private set; }

        /// <summary>The registry a release candidate is read from before it is pulled.</summary>
        public FakeRegistry? Registry { get; set; }

        /// <summary>Cancels this source while a pull runs, after Docker has the image, as a Ctrl+C mid-pull does.</summary>
        public CancellationTokenSource? CancelDuringPull { get; set; }

        public void ConfigureRuntime(
            InstallationPaths installationPaths,
            string immutableInitialReference,
            string initialImageId,
            string immutableCandidateReference,
            string immutableCandidateImageId,
            uint runtimeUid,
            uint runtimeGid,
            bool exists = true)
        {
            paths = installationPaths;
            initialReference = immutableInitialReference;
            this.initialImageId = initialImageId;
            activeImageId = exists ? initialImageId : null;
            candidateReference = immutableCandidateReference;
            candidateImageId = immutableCandidateImageId;
            uid = runtimeUid;
            gid = runtimeGid;
            running = exists;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "The synchronous read is confined to a deterministic in-memory process-runner test double.")]
        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            if (fileName == "tar")
            {
                // No lifecycle operation archives instance state any more; a tar run is counted so tests can prove it.
                if (arguments.Contains("--create", StringComparer.Ordinal)) BackupCount++;
                return new ProcessRunner().RunAsync(fileName, arguments, cancellationToken);
            }
            if (fileName == "sudo")
            {
                // Only the purge namespace handoff escalates; a test that expects no escalation leaves the hook unset.
                SudoCount++;
                Assert.IsNotNull(Sudo, "The privileged purge helper was invoked unexpectedly.");
                return Sudo(arguments, cancellationToken);
            }
            Assert.AreEqual("docker", fileName);
            if (arguments is ["context", "inspect", ..])
                return Task.FromResult(new ProcessResult(0, "unix:///var/run/docker.sock", string.Empty));
            if (arguments.Count >= 2 && arguments[0] == "--host") arguments = arguments.Skip(2).ToArray();
            if (FakeRegistry.IsRead(arguments))
                return Task.FromResult(Registry?.Answer(arguments) ?? throw new AssertFailedException("The test gave no registry."));
            if (arguments[0] == "image") ImagePreparationCount++;
            if (arguments is ["info", ..])
            {
                infoCallCount++;
                var currentDaemon = (DaemonDriftAtInfoCall is { } driftAt && infoCallCount >= driftAt) ||
                                    DriftDaemonAfterComposeUp && ComposeUpCount > 0
                    ? daemon with { Id = "replacement-daemon" }
                    : daemon;
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new
                {
                    OSType = "linux",
                    ID = currentDaemon.Id,
                    Name = currentDaemon.Name,
                    Architecture = currentDaemon.Architecture,
                    ServerVersion = currentDaemon.ServerVersion
                }), string.Empty));
            }
            if (arguments is ["compose", "version", ..]) return Task.FromResult(new ProcessResult(0, "v2", string.Empty));
            if (arguments is ["image", "pull", var pulled])
            {
                PullCount++;
                if (candidateImageId is not null) CandidateHeld?.UnionWith([pulled, candidateImageId]);
                if (CancelDuringPull is { } cancellation)
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }
            if (arguments is ["image", "load", ..] && candidateImageId is not null)
            {
                LoadCount++;
                CandidateHeld?.Add(candidateImageId);
                return Task.FromResult(new ProcessResult(0, $"Loaded image ID: {candidateImageId}\n", string.Empty));
            }
            if (arguments is ["image", "rm", var removed])
            {
                RemovedImages.Add(removed);
                if (CandidateHeld is null || !CandidateHeld.Remove(removed))
                    return Task.FromResult(new ProcessResult(1, string.Empty, $"Error: No such image: {removed}"));
                CandidateHeld.Clear();
                return Task.FromResult(new ProcessResult(0, $"Deleted: {candidateImageId}\n", string.Empty));
            }
            if (arguments is ["image", "inspect", var absentReference] && CandidateHeld is not null &&
                absentReference != initialReference && absentReference != initialImageId && !CandidateHeld.Contains(absentReference))
            {
                return Task.FromResult(new ProcessResult(1, "[]", $"Error: No such image: {absentReference}"));
            }
            if (arguments is ["image", "inspect", var inspectedReference] && candidateImageId is not null)
            {
                var isCandidate = inspectedReference == candidateReference;
                if (isCandidate) OnCandidateInspect?.Invoke();
                var inspectedImageId = isCandidate ? candidateImageId : initialImageId;
                var inspectedDigest = isCandidate ? candidateReference : initialReference;
                var labels = ImageLabels(isCandidate);
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        Id = inspectedImageId,
                        Architecture = daemon.Architecture,
                        Os = "linux",
                        RepoDigests = new[] { inspectedDigest },
                        Config = new { Labels = labels }
                    }
                }), string.Empty));
            }
            if (arguments is ["compose", ..])
            {
                if (arguments.Contains("config", StringComparer.Ordinal))
                {
                    var composePath = arguments[arguments.ToList().IndexOf("--file") + 1];
                    return Task.FromResult(new ProcessResult(0, File.ReadAllText(composePath), string.Empty));
                }
                if (arguments.Contains("stop", StringComparer.Ordinal))
                {
                    ComposeStopCount++;
                    if (RejectNextStop)
                    {
                        RejectNextStop = false;
                        return Task.FromResult(new ProcessResult(1, string.Empty, "simulated stop interruption"));
                    }
                    running = false;
                }
                if (arguments.Contains("up", StringComparer.Ordinal))
                {
                    ComposeUpCount++;
                    Events.Add("compose-up");
                    var environmentPath = arguments[arguments.ToList().IndexOf("--env-file") + 1];
                    activeImageId = File.ReadLines(environmentPath)
                        .Single(line => line.StartsWith("CAMERAAGENT_IMAGE=", StringComparison.Ordinal))["CAMERAAGENT_IMAGE=".Length..];
                    running = true;
                    OnComposeUp?.Invoke(activeImageId);
                }
                if (arguments.Contains("restart", StringComparer.Ordinal)) running = true;
                if (arguments.Contains("down", StringComparer.Ordinal))
                {
                    ComposeDownCount++;
                    activeImageId = null;
                    running = false;
                }
                if (arguments.Contains("restart", StringComparer.Ordinal)) ComposeRestartCount++;
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }
            if (arguments is ["container", "ls", ..])
            {
                var listed = RetainOwnedOrphanAfterDown && ComposeDownCount > 0 || ListOwnedOrphan ? "owned-orphan\n" : string.Empty;
                return Task.FromResult(new ProcessResult(0, listed, string.Empty));
            }
            if (arguments is ["container", "inspect", "owned-orphan", ..] && paths is not null)
            {
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        Config = new
                        {
                            Labels = new Dictionary<string, string>
                            {
                                ["io.hvo.skymonitor.instance-id"] = Path.GetFileName(paths.InstanceRoot)
                            }
                        },
                        Mounts = Array.Empty<object>()
                    }
                }), string.Empty));
            }
            if (arguments is ["container", "logs", "--tail", "200", var loggedContainer])
            {
                LoggedContainers.Add(loggedContainer);
                Events.Add($"logs:{loggedContainer}");
                return Task.FromResult(new ProcessResult(
                    0,
                    $"{loggedContainer} startup\n{loggedContainer} failure token=runner-secret",
                    string.Empty));
            }
            if (arguments is ["container", "inspect", _, ..] && activeImageId is not null && paths is not null)
            {
                var containerName = arguments[2];
                var labels = new Dictionary<string, string>
                {
                    ["com.docker.compose.project"] = $"hvo-skymonitor-{paths.InstanceRoot.Split(Path.DirectorySeparatorChar).Last().Replace("-", string.Empty, StringComparison.Ordinal)}"
                };
                if (!OmitOwnershipLabel)
                    labels["io.hvo.skymonitor.instance-id"] = paths.InstanceRoot.Split(Path.DirectorySeparatorChar).Last();
                if (localRunner && containerName.EndsWith("-replay", StringComparison.Ordinal))
                {
                    return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new[]
                    {
                        new
                        {
                            State = new { Running = running, Health = new { Status = "healthy" } },
                            Image = activeImageId,
                            Config = new
                            {
                                User = $"{uid}:{gid}",
                                Labels = labels,
                                Entrypoint = ReplayRunnerEntrypoint
                            },
                            HostConfig = new
                            {
                                ReadonlyRootfs = true,
                                Privileged = false,
                                CapDrop = DroppedCapabilities,
                                SecurityOpt = ReplayRunnerSecurityOptions,
                                NetworkMode = "none",
                                Tmpfs = new Dictionary<string, string>
                                {
                                    ["/tmp"] = "rw,nosuid,nodev,noexec,mode=1777,size=67108864"
                                }
                            },
                            Mounts = new[]
                            {
                                new
                                {
                                    Type = "bind",
                                    Source = Path.Combine(paths.ConfigRoot, "secrets", "replay-runner-auth-key"),
                                    Destination = "/run/hvo-secrets/replay-runner-auth-key",
                                    RW = false
                                },
                                new
                                {
                                    Type = "bind",
                                    Source = Path.Combine(paths.StateRoot, "replay-runner"),
                                    Destination = "/run/hvo-replay",
                                    RW = true
                                },
                            }
                        }
                    }), string.Empty));
                }
                var mounts = new List<object>
                {
                    new { Source = Path.Combine(paths.ConfigRoot, "camera-module.json"), Destination = "/app/cameraagent.deploy.json", RW = false },
                    new { Source = MountedCatalogRoot(paths), Destination = "/app/catalog", RW = false },
                    new { Source = Path.Combine(paths.StateRoot, "identity"), Destination = "/app/App_Data", RW = true }
                };
                mounts.Add(new { Source = Path.Combine(paths.ConfigRoot, "secrets"), Destination = "/run/hvo-secrets", RW = false });
                foreach (var source in CameraAgentStateLayout.WritableBindSources(paths.StateRoot, HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.InProcess)
                             .Where(static source => !source.ContainerPath.Equals("/app/App_Data", StringComparison.Ordinal)))
                    mounts.Add(new { Source = source.HostPath, Destination = source.ContainerPath, RW = true });
                if (localRunner)
                {
                    mounts.Add(new
                    {
                        Source = Path.Combine(paths.StateRoot, "replay-runner"),
                        Destination = "/run/hvo-replay",
                        RW = true
                    });
                }
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        State = new { Running = running, Health = new { Status = Unhealthy ? "unhealthy" : "healthy" } },
                        Image = activeImageId,
                        Config = new { User = $"{uid}:{gid}", Labels = labels },
                        HostConfig = new
                        {
                            ReadonlyRootfs = true, Privileged = false,
                            Memory = 8L * 1024 * 1024 * 1024, MemorySwap = 9L * 1024 * 1024 * 1024,
                            CapDrop = RetainCapabilities ? Array.Empty<string>() : DroppedCapabilities,
                            SecurityOpt = ReplayRunnerSecurityOptions,
                            PortBindings = new Dictionary<string, object>
                            {
                                ["8080/tcp"] = new[] { new { HostIp = "127.0.0.1", HostPort = WrongPort ? "9999" : "5130" } }
                            }
                        },
                        Mounts = mounts
                    }
                }), string.Empty));
            }
            if (arguments is ["container", "inspect", _, ..])
                return Task.FromResult(new ProcessResult(1, string.Empty, $"Error response from daemon: No such container: {arguments[2]}"));
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }

        private int infoCallCount;
    }

    private sealed class FakeLifecycleClient : ICameraAgentLifecycleClient
    {
        public bool RejectNextResume { get; set; }
        public long DrainedProcessingPending { get; set; }
        public long DrainedReplayPending { get; set; }
        public long DrainedReplayLeased { get; set; }
        public int ResumeCount { get; private set; }
        public int PauseCount { get; private set; }
        public int ConfirmCount { get; private set; }
        public int RecoveryResumeCalls { get; private set; }
        public bool LoseRecoveryAcknowledgement { get; set; }
        public bool RejectRecoveryRead { get; set; }
        public TimeSpan RecoveryReceiptClockOffset { get; set; }
        public HashSet<Guid> RecoveryCommands { get; } = [];
        public LifecycleContinuity RecoveryContinuity { get; set; } =
            new("Paused", 2, 1812, 0, 0, 0, 0, 0, 0, 0, 0, ReplayCatalogIndependent: true);

        public Task<LifecycleContinuity> ReadContinuityAsync(string verificationToken, CancellationToken cancellationToken)
        {
            Assert.AreEqual("lifecycle-token", verificationToken);
            if (RejectRecoveryRead) throw new InstallerException("CameraAgent rejected the lifecycle state read with status 401.");
            return Task.FromResult(RecoveryContinuity);
        }

        public Task<LifecycleResumeReceipt> ResumeRecoveryAsync(Guid operationId, Guid commandId, long expectedVersion, string verificationToken, CancellationToken cancellationToken)
        {
            Assert.AreEqual("lifecycle-token", verificationToken);
            Assert.AreEqual(2L, expectedVersion);
            RecoveryResumeCalls++;
            var replayed = !RecoveryCommands.Add(commandId);
            if (!replayed)
            {
                Assert.AreEqual(expectedVersion, RecoveryContinuity.CaptureVersion);
                RecoveryContinuity = RecoveryContinuity with { CaptureState = "Running", CaptureVersion = expectedVersion + 1 };
            }
            if (LoseRecoveryAcknowledgement)
            {
                LoseRecoveryAcknowledgement = false;
                throw new HttpRequestException("simulated lost recovery acknowledgement");
            }
            return Task.FromResult(new LifecycleResumeReceipt("Running", expectedVersion + 1, true, replayed,
                DateTimeOffset.UtcNow + RecoveryReceiptClockOffset, DateTimeOffset.UtcNow + RecoveryReceiptClockOffset));
        }

        public Task<LifecycleContinuity> PauseAndDrainAsync(
            Guid operationId,
            string verificationToken,
            CancellationToken cancellationToken)
        {
            Assert.AreEqual("lifecycle-token", verificationToken);
            PauseCount++;
            return Task.FromResult(new LifecycleContinuity("Paused", 42, 100, 0, 0, 0, 0, DrainedProcessingPending, 0, 0, 0,
                ReplayPending: DrainedReplayPending, ReplayLeased: DrainedReplayLeased));
        }

        public Task<LifecycleContinuity> ConfirmDrainedAsync(string verificationToken, CancellationToken cancellationToken)
        {
            Assert.AreEqual("lifecycle-token", verificationToken);
            ConfirmCount++;
            return Task.FromResult(new LifecycleContinuity("Paused", 42, 100, 0, 0, 0, 0, 0, 0, 0, 0,
                ReplayPending: DrainedReplayPending, ReplayLeased: DrainedReplayLeased));
        }

        public Task ResumeAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken)
        {
            Assert.AreEqual("lifecycle-token", verificationToken);
            ResumeCount++;
            if (RejectNextResume)
            {
                RejectNextResume = false;
                throw new HttpRequestException("simulated lost resume acknowledgement");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOwnerClient(Guid expectedAgentId) : IOwnerBootstrapClient
    {
        public bool RejectNextVerification { get; set; }
        public string CurrentOwnerBootstrapState { get; set; } = "owner-password-change-required";
        public Queue<string> VerificationStates { get; } = new();

        public Task WaitForHealthAsync(
            ApprovedCatalogContract expectedCatalog, CancellationToken cancellationToken, TimeSpan? timeout = null) => Task.CompletedTask;
        public Task<string> ReadStateAsync(string ownerEmail, string password, CancellationToken cancellationToken)
            => Task.FromResult("owner-password-change-required");
        public Task<string> ReadInstallationStateAsync(string verificationToken, CancellationToken cancellationToken)
            => Task.FromResult(CurrentOwnerBootstrapState);
        public Task<string> VerifyInstallationAsync(
            string verificationToken,
            InstallationVerificationExpectation expectation,
            CancellationToken cancellationToken)
        {
            if (RejectNextVerification)
            {
                RejectNextVerification = false;
                throw new InstallerException("simulated candidate verification failure");
            }
            if (expectation.AgentId != expectedAgentId.ToString("D"))
            {
                throw new InstallerException("simulated application identity mismatch");
            }
            var currentState = VerificationStates.TryDequeue(out var state)
                ? state
                : CurrentOwnerBootstrapState;
            if (!OwnerBootstrapClient.IsAllowedOwnerBootstrapState(
                    expectation.OwnerBootstrapState,
                    currentState,
                    expectation.AllowCompletedPasswordReplacement))
            {
                throw new InstallerException("simulated owner bootstrap state mismatch");
            }
            return Task.FromResult(currentState);
        }
    }
}
