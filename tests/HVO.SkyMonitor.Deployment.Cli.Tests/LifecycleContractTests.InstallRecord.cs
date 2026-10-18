using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The instance lifecycle commands and the command surface of #1315: each completed operation settles the instance's
/// install record entry before it records completion (a record that cannot be written leaves the operation resumable),
/// <c>--plan</c> reports what each command would change and changes nothing, and each gate that decides not to go ahead
/// is a refusal (exit 3) that changes nothing.
/// </summary>
public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeThenRollback_SettleTheInstallRecordEntryEachTime()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, installedImage: CurrentStateContract);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);
        var location = InstallRecordLocation.For(fixture.Root, InstallRequest.DefaultProductRoot);

        await CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);
        var upgraded = (await InstallRecordStore.ReadAsync(location, CancellationToken.None))!.Instances.Single();
        await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None,
            error: TextWriter.Null);
        var rolledBack = (await InstallRecordStore.ReadAsync(location, CancellationToken.None))!.Instances.Single();

        Assert.AreEqual(Path.Combine(fixture.Root, "operations", "install.json"), location.Path);
        Assert.AreEqual(fixture.InstanceId, upgraded.InstanceId);
        Assert.AreEqual(CandidateImageId, upgraded.ImageId);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, upgraded.PreviousImageId);
        Assert.AreEqual(InstanceLifecycleCondition.Installed, upgraded.Condition);
        // An image given by --image-ref names no release, so no version is recorded for it.
        Assert.IsNull(upgraded.Version);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, rolledBack.ImageId);
        Assert.AreEqual(CandidateImageId, rolledBack.PreviousImageId);
        Assert.IsNull(rolledBack.PreviousVersion);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_InstallRecordThatCannotBeWritten_LeavesTheCommittedOperationForResume()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, installedImage: CurrentStateContract);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);
        var location = InstallRecordLocation.For(fixture.Root, InstallRequest.DefaultProductRoot);
        // A record nobody can explain is never silently overwritten.
        await File.WriteAllTextAsync(location.Path, "not a record");
        File.SetUnixFileMode(location.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var lifecycle = new FakeLifecycleClient();

        var interrupted = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(interrupted.Message, "--resume", StringComparison.Ordinal);
        Assert.IsFalse(interrupted.IsRefusal);
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(LifecycleOperationPhase.Committed, journal.Phase);
        Assert.AreNotEqual(InstallationStatus.Completed, journal.Status);
        // Capture was resumed before the record was attempted, so a record failure never holds capture paused.
        Assert.AreEqual(lifecycle.PauseCount, lifecycle.ResumeCount);

        File.Delete(location.Path);
        var resumed = await CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false) with { Resume = true }, fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", resumed.Outcome);
        Assert.AreEqual(CandidateImageId, (await InstallRecordStore.ReadAsync(location, CancellationToken.None))!.Instances.Single().ImageId);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UninstallThenPurge_RecordTheConditionThenRemoveTheEntry()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid);
        var location = InstallRecordLocation.For(fixture.Root, InstallRequest.DefaultProductRoot);

        await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Uninstall), fixture.Runner, _ => new FakeLifecycleClient(), null,
            fixture.Uid, fixture.Gid, CancellationToken.None);
        var uninstalled = (await InstallRecordStore.ReadAsync(location, CancellationToken.None))!.Instances.Single();
        await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Purge) with { ConfirmationInstanceId = fixture.InstanceId }, fixture.Runner,
            null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual(InstanceLifecycleCondition.Uninstalled, uninstalled.Condition);
        Assert.IsEmpty((await InstallRecordStore.ReadAsync(location, CancellationToken.None))!.Instances);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_Plan_ListsWhatItWouldChangeAndChangesNothing()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, installedImage: CurrentStateContract);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);
        var manifest = await File.ReadAllTextAsync(fixture.Paths.ManifestPath);

        var planned = await CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false) with { DryRun = true }, fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("planned", planned.Outcome);
        var container = planned.Plan!.Items.Single(static item => item.Kind == "container");
        Assert.AreEqual(DeploymentPlanAction.Change, container.Action);
        StringAssert.Contains(container.Detail!, $"to {CandidateImageId}", StringComparison.Ordinal);
        Assert.AreEqual(DeploymentPlanAction.Unchanged, planned.Plan.Items.Single(static item => item.Kind == "folder").Action);
        Assert.IsFalse(planned.Plan.Items.Any(static item => item.Kind == "snapshot"), "an unchanged state contract takes no snapshot");
        Assert.AreEqual(manifest, await File.ReadAllTextAsync(fixture.Paths.ManifestPath));
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
        Assert.IsFalse(File.Exists(InstallRecordLocation.For(fixture.Root, InstallRequest.DefaultProductRoot).Path));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UninstallAndPurge_Plan_ListWhatTheyWouldRemove()
    {
        using var installed = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        using var uninstalled = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Uninstalled);

        var uninstall = await CameraAgentLifecycleManager.ExecuteAsync(
            installed.Request(LifecycleOperationKind.Uninstall) with { DryRun = true }, installed.Runner, null, null,
            installed.Uid, installed.Gid, CancellationToken.None);
        var purge = await CameraAgentLifecycleManager.ExecuteAsync(
            uninstalled.Request(LifecycleOperationKind.Purge) with { DryRun = true, ConfirmationInstanceId = uninstalled.InstanceId },
            uninstalled.Runner, null, null, uninstalled.Uid, uninstalled.Gid, CancellationToken.None);

        Assert.AreEqual(DeploymentPlanAction.Remove, uninstall.Plan!.Items.Single(static item => item.Kind == "container").Action);
        Assert.IsTrue(uninstall.Plan.Items.Any(static item => item.Action == DeploymentPlanAction.Unchanged && item.Detail == "state is preserved"));
        Assert.AreEqual(DeploymentPlanAction.Remove, purge.Plan!.Items[0].Action);
        Assert.AreEqual(uninstalled.Paths.InstanceRoot, purge.Plan.Items[0].Target);
        Assert.IsTrue(Directory.Exists(uninstalled.Paths.InstanceRoot));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task LifecycleGates_ThatDecideNotToGoAhead_AreRefusalsThatChangeNothing()
    {
        using var installed = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        using var uninstalled = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Uninstalled);

        var noPrevious = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            installed.Request(LifecycleOperationKind.Rollback), installed.Runner, null, null, installed.Uid, installed.Gid, CancellationToken.None));
        var notUninstalled = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            installed.Request(LifecycleOperationKind.Reinstall), installed.Runner, null, null, installed.Uid, installed.Gid, CancellationToken.None));
        var installedPurge = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            installed.Request(LifecycleOperationKind.Purge) with { ConfirmationInstanceId = installed.InstanceId }, installed.Runner,
            null, null, installed.Uid, installed.Gid, CancellationToken.None));
        var uninstalledUpgrade = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(uninstalled, assertMigration: false), uninstalled.Runner, null, null, uninstalled.Uid, uninstalled.Gid,
            CancellationToken.None));
        var asRoot = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            installed.Request(LifecycleOperationKind.Uninstall), installed.Runner, null, null, 0, 0, CancellationToken.None));

        foreach (var refusal in new[] { noPrevious, notUninstalled, installedPurge, uninstalledUpgrade, asRoot })
        {
            Assert.IsTrue(refusal.IsRefusal, refusal.Message);
        }
        Assert.AreEqual(InstanceLifecycleCondition.Installed,
            (await CameraAgentLifecycleManager.ReadManifestAsync(installed.Paths.ManifestPath, CancellationToken.None)).LifecycleCondition);
        Assert.IsFalse(File.Exists(InstallRecordLocation.For(installed.Root, InstallRequest.DefaultProductRoot).Path));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_ToTheImageAlreadyActive_IsARefusal()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, installedImage: CurrentStateContract);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid);

        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with { ImageReference = fixture.Manifest.Image.ImmutableReference, NoDownload = true },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.IsTrue(refused.IsRefusal);
        StringAssert.Contains(refused.Message, "already active", StringComparison.Ordinal);
    }
}
