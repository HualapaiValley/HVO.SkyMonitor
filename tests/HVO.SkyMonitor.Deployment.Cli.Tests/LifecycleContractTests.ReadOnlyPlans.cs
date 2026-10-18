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
