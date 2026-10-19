using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Lifecycle plans (#1315 PR-1355-R1): a plan creates no directory, lock or temporary file (F1), and it admits exactly what
/// the command admits, so a journal that makes the command refuse makes its plan refuse the same way (F2). A plan cannot
/// be combined with <c>--resume</c>, so the journal gates a plan meets are those of a fresh command.
/// </summary>
public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DoNotParallelize]
    public async Task LifecyclePlans_CreateNoDirectoryLockOrTemporaryFile()
    {
        using var installed = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, installedImage: CurrentStateContract);
        using var uninstalled = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Uninstalled);
        installed.Runner.ConfigureRuntime(installed.Paths, installed.Manifest.Image.ImmutableReference, installed.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, installed.Uid, installed.Gid);
        var installedBefore = FileSystemActivity.Snapshot(installed.Root);
        var uninstalledBefore = FileSystemActivity.Snapshot(uninstalled.Root);

        var plans = new (string Name, Func<Task<LifecycleResult>> Run)[]
        {
            ("upgrade", () => CameraAgentLifecycleManager.ExecuteAsync(
                UpgradeRequest(installed, assertMigration: false) with { DryRun = true }, installed.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(installed.ApplicationIdentity), installed.Uid, installed.Gid, CancellationToken.None)),
            ("uninstall", () => CameraAgentLifecycleManager.ExecuteAsync(
                installed.Request(LifecycleOperationKind.Uninstall) with { DryRun = true }, installed.Runner, null, null,
                installed.Uid, installed.Gid, CancellationToken.None)),
            ("reinstall", () => CameraAgentLifecycleManager.ExecuteAsync(
                uninstalled.Request(LifecycleOperationKind.Reinstall) with { DryRun = true }, uninstalled.Runner, null, null,
                uninstalled.Uid, uninstalled.Gid, CancellationToken.None)),
            ("purge", () => CameraAgentLifecycleManager.ExecuteAsync(
                uninstalled.Request(LifecycleOperationKind.Purge) with { DryRun = true, ConfirmationInstanceId = uninstalled.InstanceId },
                uninstalled.Runner, null, null, uninstalled.Uid, uninstalled.Gid, CancellationToken.None))
        };
        foreach (var (name, run) in plans)
        {
            var (planned, created) = await FileSystemActivity.WithPrivateTemporaryDirectoryAsync(run);

            Assert.AreEqual("planned", planned.Outcome, name);
            Assert.IsEmpty(created, $"the {name} plan created temporary files: {string.Join(", ", created)}");
            FileSystemActivity.AssertUnchanged(installedBefore, FileSystemActivity.Snapshot(installed.Root), $"the {name} plan changed the product root");
            FileSystemActivity.AssertUnchanged(uninstalledBefore, FileSystemActivity.Snapshot(uninstalled.Root), $"the {name} plan changed the product root");
        }
    }

    /// <summary>
    /// <c>install.sh</c> hands <c>cameraagent upgrade</c> a release from the registry (#1311), which Docker does not hold
    /// before the upgrade pulls it. Its plan reads and checks the candidate from the registry and pulls nothing.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_PlanOfARegistryReleaseDockerDoesNotHoldYet_IsPlannedFromTheRegistryAndPullsNothing()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        using var release = CandidateRelease(fixture, out var candidateImageId);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            release.DigestReference, candidateImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.Registry = new FakeRegistry(release.RegistryContent);
        fixture.Runner.CandidateHeld = [];
        var before = FileSystemActivity.Snapshot(fixture.Root);

        var planned = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                Release = release.Folder,
                ImageSource = ReleaseImageSource.Registry,
                DryRun = true
            },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("planned", planned.Outcome);
        StringAssert.Contains(planned.Plan!.Items.Single(static item => item.Kind == "container").Detail!, candidateImageId, StringComparison.Ordinal);
        Assert.IsGreaterThan(0, fixture.Runner.Registry.Reads);
        Assert.AreEqual(0, fixture.Runner.PullCount);
        Assert.IsEmpty(fixture.Runner.CandidateHeld!);
        FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), "the upgrade plan changed the product root");
    }

    /// <summary>
    /// Under Docker's containerd image store the upgrade plan names the candidate by the release's image digest, the ID
    /// Docker gives it once it is pulled and the one the upgrade and <c>status</c> then report, not by its configuration
    /// digest (#1384).
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_PlanUnderTheContainerdImageStore_NamesTheCandidateByTheReleasesImageDigest()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        using var release = CandidateRelease(fixture, out var configDigest);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            release.DigestReference, release.Digest, fixture.Uid, fixture.Gid);
        fixture.Runner.Registry = new FakeRegistry(release.RegistryContent);
        fixture.Runner.CandidateHeld = [];
        fixture.Runner.ContainerdImageStore = true;

        var planned = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                Release = release.Folder,
                ImageSource = ReleaseImageSource.Registry,
                DryRun = true
            },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("planned", planned.Outcome);
        var container = planned.Plan!.Items.Single(static item => item.Kind == "container").Detail!;
        StringAssert.Contains(container, release.Digest, StringComparison.Ordinal);
        Assert.DoesNotContain(configDigest, container, StringComparison.Ordinal);
        Assert.AreEqual(0, fixture.Runner.PullCount);
    }

    /// <summary>
    /// A lifecycle command decides every refusal it can before its first write (#1315 PR-1355-R2 R2-3): an upgrade to the
    /// image already active, a rollback with no previous image, a reinstall or purge of an installed instance and an
    /// upgrade of an uninstalled one are each refused with the whole tree unchanged, no lock file and no journal.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task LifecycleRefusals_AreDecidedBeforeAnyWrite()
    {
        using var installed = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, installedImage: CurrentStateContract);
        using var uninstalled = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Uninstalled);
        installed.Runner.ConfigureRuntime(installed.Paths, installed.Manifest.Image.ImmutableReference, installed.Manifest.Image.ImageId,
            installed.Manifest.Image.ImmutableReference, installed.Manifest.Image.ImageId, installed.Uid, installed.Gid);
        var refusals = new (string Name, LifecycleFixture Fixture, LifecycleRequest Request, string Message)[]
        {
            ("already-active", installed,
                installed.Request(LifecycleOperationKind.Upgrade) with
                {
                    ImageReference = installed.Manifest.Image.ImmutableReference,
                    NoDownload = true
                },
                "The candidate image is already active."),
            ("no-previous", installed, installed.Request(LifecycleOperationKind.Rollback), "No previous image is retained for rollback."),
            ("reinstall-installed", installed, installed.Request(LifecycleOperationKind.Reinstall), "Reinstall requires a preserved uninstalled instance."),
            ("purge-installed", installed,
                installed.Request(LifecycleOperationKind.Purge) with { ConfirmationInstanceId = installed.InstanceId },
                "Purge requires a completed preserve-by-default uninstall."),
            ("upgrade-uninstalled", uninstalled, UpgradeRequest(uninstalled, assertMigration: false),
                "An uninstalled instance must be reinstalled before image lifecycle operations.")
        };
        foreach (var (name, fixture, request, message) in refusals)
        {
            var before = FileSystemActivity.Snapshot(fixture.Root);

            var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                request, fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
                fixture.Uid, fixture.Gid, CancellationToken.None));

            Assert.IsTrue(refused.IsRefusal, name);
            Assert.AreEqual(message, refused.Message, name);
            FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), $"the {name} refusal changed the product root");
        }
    }

    /// <summary>
    /// A gate that only an imported candidate can decide runs after the journal entry, so its refusal is settled in the
    /// journal and exits 1, not 3 (#1315 PR-1355-R2 R2-3): here a candidate pulled once the upgrade has begun declares an
    /// old state contract.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_RefusalAfterTheJournalEntry_IsAJournaledFailure()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, installedImage: CurrentStateContract);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.CandidateLabels["io.hvo.skymonitor.state-compatibility"] = "cameraagent-state-v1";
        fixture.Runner.CandidateHeld = [];

        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with { ImageReference = CandidateReference, NoDownload = false },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.IsFalse(failure.IsRefusal, failure.Message);
        StringAssert.StartsWith(failure.Message, "The candidate image declares state compatibility 'cameraagent-state-v1'", StringComparison.Ordinal);
        Assert.AreEqual(1, fixture.Runner.PullCount);
        var journal = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(InstallationStatus.Failed, journal!.Status);
        Assert.AreEqual(CameraAgentLifecycleManager.RefusedFailureCode, journal.FailureCode);
        Assert.IsFalse(journal.MutationStarted);
    }

    /// <summary>
    /// An upgrade from a registry release Docker does not hold yet, which its state preflight refuses (here an unreadable
    /// Identity database), is refused before anything is pulled, like its plan (#1315 PR-1355-R3 R3-1): every check that
    /// only reads comes before the pull, so the refusal leaves Docker holding what it held and the tree unchanged.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_RegistryReleaseTheStatePreflightRefuses_IsRefusedBeforeAnythingIsPulled()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        using var release = CandidateRelease(fixture, out var candidateImageId);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            release.DigestReference, candidateImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.Registry = new FakeRegistry(release.RegistryContent);
        fixture.Runner.CandidateHeld = [];
        var identityRoot = Path.Combine(fixture.Paths.StateRoot, "identity");
        Directory.CreateDirectory(identityRoot);
        File.SetUnixFileMode(identityRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var identity = Path.Combine(identityRoot, "cameraagent_identity.db");
        await File.WriteAllBytesAsync(identity, new byte[4096]);
        File.SetUnixFileMode(identity, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            Release = release.Folder,
            ImageSource = ReleaseImageSource.Registry,
            MigrationBackwardCompatible = true
        };
        var before = FileSystemActivity.Snapshot(fixture.Root);

        foreach (var dryRun in new[] { true, false })
        {
            var name = dryRun ? "the plan" : "the upgrade";
            var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                request with { DryRun = dryRun }, fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None));

            Assert.IsTrue(refused.IsRefusal, $"{name}: {refused.Message}");
            StringAssert.StartsWith(refused.Message, "CameraAgent state preflight rejected", StringComparison.Ordinal, name);
            Assert.AreEqual(0, fixture.Runner.PullCount, $"{name} pulled before its state preflight refused");
            Assert.IsEmpty(fixture.Runner.CandidateHeld!, $"{name} left Docker holding what it pulled");
            Assert.IsEmpty(fixture.Runner.RemovedImages, name);
            FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), $"{name} changed the product root");
        }
    }

    /// <summary>
    /// A refusal that comes after the registry pull, under the instance lock and before anything is written, still says
    /// nothing changed, so the pulled image is removed again (#1315 PR-1355-R3 R3-1). Here another run uninstalls the
    /// instance while this upgrade pulls, and the upgrade's check under the lock refuses it. An installed instance has
    /// both lock files from its install, so taking them writes nothing.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_RefusalUnderTheLockAfterTheRegistryPull_RemovesThePulledImage()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        using var release = CandidateRelease(fixture, out var candidateImageId);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            release.DigestReference, candidateImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.Registry = new FakeRegistry(release.RegistryContent);
        fixture.Runner.CandidateHeld = [];
        SafeFileSystem.CreateOwnerDirectory(fixture.Paths.OperationsRoot);
        using (OperationLock.Acquire(Path.Combine(fixture.Paths.OperationsRoot, "deployment.lock"), cancellationToken: CancellationToken.None))
        using (OperationLock.Acquire(Path.Combine(fixture.Paths.InstanceRoot, ".deployment.lock"), cancellationToken: CancellationToken.None))
        {
        }
        var uninstalled = fixture.Manifest with { LifecycleCondition = InstanceLifecycleCondition.Uninstalled };
        fixture.Runner.OnPull = () => SafeFileSystem.WriteTextAtomic(
            fixture.Paths.ManifestPath, JsonSerializer.Serialize(uninstalled, DeploymentJsonContext.Default.InstanceManifest));

        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                Release = release.Folder,
                ImageSource = ReleaseImageSource.Registry,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.IsTrue(refused.IsRefusal, refused.Message);
        Assert.AreEqual("An uninstalled instance must be reinstalled before image lifecycle operations.", refused.Message);
        Assert.AreEqual(1, fixture.Runner.PullCount);
        CollectionAssert.Contains(fixture.Runner.RemovedImages, release.DigestReference);
        Assert.IsEmpty(fixture.Runner.CandidateHeld!, "the refusal left Docker holding what the upgrade pulled");
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath), "the refusal opened the journal");
    }

    /// <summary>
    /// A refusal says that nothing changed, so it stands only once Docker no longer holds the image the run pulled
    /// (#1315 PR-1355-R4 R4-1). When Docker refuses the removal, or its images cannot be listed afterwards, the refusal is
    /// a failure (exit 1) that names the images and how to remove them. Absence is proven only by a listing that succeeds
    /// and reads completely: an inspect that fails while <c>docker info</c> answers proves nothing, nor does a listing that
    /// fails or does not parse (R5-1). This holds both for a refusal under the lock after the pull and for the pull's own
    /// check of the image Docker resolved.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("refused-under-the-lock", "removal-fails")]
    [DataRow("refused-under-the-lock", "read-back-fails")]
    [DataRow("refused-by-the-pull-check", "removal-fails")]
    [DataRow("refused-under-the-lock", "removal-and-inspect-fail")]
    [DataRow("refused-under-the-lock", "listing-fails")]
    [DataRow("refused-under-the-lock", "listing-malformed")]
    public async Task UpgradeAsync_RefusalWhosePulledImageIsNotProvedRemoved_IsAFailureNamingTheImage(string refusal, string fault)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        using var release = CandidateRelease(fixture, out var candidateImageId);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            release.DigestReference, candidateImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.Registry = new FakeRegistry(release.RegistryContent);
        fixture.Runner.CandidateHeld = [];
        fixture.Runner.FailImageRemoval = fault is "removal-fails" or "removal-and-inspect-fail";
        fixture.Runner.FailDaemonReadAfterRemoval = fault == "read-back-fails";
        // Removal and inspection fail while 'docker info' answers, as with a failing containerd image store (R5-1).
        fixture.Runner.FailImageInspectAfterRemoval = fault == "removal-and-inspect-fail";
        fixture.Runner.FailImageListing = fault == "listing-fails";
        fixture.Runner.MalformedImageListing = fault == "listing-malformed";
        if (refusal == "refused-under-the-lock")
        {
            SafeFileSystem.CreateOwnerDirectory(fixture.Paths.OperationsRoot);
            using (OperationLock.Acquire(Path.Combine(fixture.Paths.OperationsRoot, "deployment.lock"), cancellationToken: CancellationToken.None))
            using (OperationLock.Acquire(Path.Combine(fixture.Paths.InstanceRoot, ".deployment.lock"), cancellationToken: CancellationToken.None))
            {
            }
            var uninstalled = fixture.Manifest with { LifecycleCondition = InstanceLifecycleCondition.Uninstalled };
            fixture.Runner.OnPull = () => SafeFileSystem.WriteTextAtomic(
                fixture.Paths.ManifestPath, JsonSerializer.Serialize(uninstalled, DeploymentJsonContext.Default.InstanceManifest));
        }
        else
        {
            // The registry's metadata is the release's, but Docker labels the image it pulled as another version.
            fixture.Runner.CandidateLabels["org.opencontainers.image.version"] = "9.9.9";
        }

        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                Release = release.Folder,
                ImageSource = ReleaseImageSource.Registry,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        var name = $"{refusal}, {fault}";
        Assert.IsFalse(failure.IsRefusal, $"{name}: an image left in Docker must not be reported as nothing changed: {failure.Message}");
        Assert.IsTrue(failure.InnerException is InstallerException { IsRefusal: true }, name);
        Assert.AreEqual(1, fixture.Runner.PullCount, name);
        CollectionAssert.Contains(fixture.Runner.RemovedImages, release.DigestReference, name);
        StringAssert.Contains(failure.Message, release.DigestReference, StringComparison.Ordinal, name);
        StringAssert.Contains(failure.Message, "docker image rm", StringComparison.Ordinal, name);
        if (fault is "removal-fails" or "removal-and-inspect-fail")
        {
            CollectionAssert.IsSubsetOf(new[] { release.DigestReference, candidateImageId }, fixture.Runner.CandidateHeld!.ToArray(), name);
            StringAssert.Contains(failure.Message, candidateImageId, StringComparison.Ordinal, name);
        }
        else
        {
            // Nothing could be proven, so the removal command names every image the run imported (R5-1).
            StringAssert.Contains(failure.Message, $"docker image rm {release.DigestReference}", StringComparison.Ordinal, name);
        }
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath), $"{name}: the failure opened the journal");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task LifecyclePlans_AreRefusedByTheJournalGatesTheirCommandsAreRefusedBy()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var uninstall = fixture.Request(LifecycleOperationKind.Uninstall);

        // An upgrade that had started mutating when it was interrupted demands --resume of every other command, plan included.
        var interrupted = new LifecycleOperationState(
            DeploymentSchemaVersions.LifecycleOperation, Guid.NewGuid(), LifecycleOperationKind.Upgrade,
            fixture.InstanceId, new string('5', 64), LifecycleOperationPhase.Mutating, InstallationStatus.Running,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, fixture.Manifest.Image,
            fixture.Manifest.Image with { ImageId = $"sha256:{new string('6', 64)}" }, fixture.Manifest.Catalog,
            MutationStarted: true);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.LifecycleStatePath, interrupted, DeploymentJsonContext.Default.LifecycleOperationState, CancellationToken.None);
        var command = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            uninstall, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));
        var before = FileSystemActivity.Snapshot(fixture.Root);
        var plan = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            uninstall with { DryRun = true }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.IsTrue(command.IsRefusal);
        Assert.IsTrue(plan.IsRefusal);
        Assert.AreEqual("An incomplete lifecycle operation exists; rerun the same command with --resume.", command.Message);
        Assert.AreEqual(command.Message, plan.Message);
        FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), "a refused plan changed the product root");
    }
}
