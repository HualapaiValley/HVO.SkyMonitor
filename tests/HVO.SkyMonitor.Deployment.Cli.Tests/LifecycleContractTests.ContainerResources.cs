using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The CameraAgent container limits across lifecycle operations: an upgrade moves an instance recorded without limits,
/// and every limit the operator did not set, to the installer default and keeps every limit the operator set; rollback
/// and reinstall keep the limits the instance records; a Compose file that disagrees with its record is refused.
/// </summary>
public sealed partial class LifecycleContractTests
{
    private const long LimitGibibyte = 1024L * 1024 * 1024;
    private const string OriginalLimits = "    cpus: \"1\"\n    mem_limit: 1G\n    pids_limit: 256\n";
    private const string DefaultLimits = "    cpus: \"2\"\n    mem_limit: 4G\n    pids_limit: 256\n";

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_InstanceRecordedWithoutLimits_MovesToTheDefaultsAndRetainsTheOriginalForRollback()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var composePath = Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml");
        var originalCompose = await File.ReadAllTextAsync(composePath);
        StringAssert.Contains(originalCompose, OriginalLimits, StringComparison.Ordinal);
        Assert.IsNull(fixture.Manifest.Resources);

        await UpgradeForLimitsAsync(fixture);

        var upgradedCompose = await File.ReadAllTextAsync(composePath);
        var upgraded = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        Assert.AreEqual(new ContainerResourceLimits(2m, 4 * LimitGibibyte, 256, false, false), upgraded.Resources);
        Assert.AreEqual(originalCompose.Replace(OriginalLimits, DefaultLimits, StringComparison.Ordinal), upgradedCompose);
        Assert.AreEqual(ComposeDeployment.ComputeSha256(upgradedCompose), upgraded.ComposeModelSha256);
        Assert.AreEqual(upgraded.ComposeModelSha256, result.ComposeModelSha256);
        // Rollback history keeps the exact model the previous image ran, authenticated by its recorded identity.
        Assert.AreEqual(fixture.Manifest.ComposeModelSha256, upgraded.PreviousComposeModelSha256);
        Assert.AreEqual(
            originalCompose,
            await File.ReadAllTextAsync(Path.Combine(fixture.Paths.DeploymentStateRoot, "rollback", "previous-compose.yml")));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_KeepsOperatorLimitsAndMovesDefaultLimitsToTheInstallerDefault()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        // The memory limit followed an earlier default; the CPU limit was set by the operator.
        await RecordLimitsAsync(fixture, new ContainerResourceLimits(3m, 5 * LimitGibibyte, 256, true, false));

        await UpgradeForLimitsAsync(fixture);

        var upgraded = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual(new ContainerResourceLimits(3m, 4 * LimitGibibyte, 256, true, false), upgraded.Resources);
        StringAssert.Contains(
            await File.ReadAllTextAsync(Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml")),
            "    cpus: \"3\"\n    mem_limit: 4G\n    pids_limit: 256\n",
            StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAndRollback_OperatorLimits_SurviveWithTheExactComposeModel()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var pinned = new ContainerResourceLimits(3.5m, 6 * LimitGibibyte, 256, true, true);
        await RecordLimitsAsync(fixture, pinned);
        var composePath = Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml");
        var pinnedCompose = await File.ReadAllTextAsync(composePath);
        var pinnedModel = ComposeDeployment.ComputeSha256(pinnedCompose);

        await UpgradeForLimitsAsync(fixture);

        var upgraded = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual(pinned, upgraded.Resources);
        Assert.AreEqual(pinnedCompose, await File.ReadAllTextAsync(composePath));
        Assert.AreEqual(pinnedModel, upgraded.ComposeModelSha256);

        await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback),
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);

        var rolledBack = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, rolledBack.Image.ImageId);
        Assert.AreEqual(pinned, rolledBack.Resources);
        Assert.AreEqual(pinnedCompose, await File.ReadAllTextAsync(composePath));
        Assert.AreEqual(pinnedModel, rolledBack.ComposeModelSha256);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_ComposeThatDisagreesWithTheRecordedLimits_IsRefusedBeforeAnyMutation()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        // The manifest records the defaults, but the authenticated Compose file still carries the original lines.
        await RecordLimitsAsync(fixture, CameraAgentContainerResources.Default, CameraAgentContainerResources.Legacy);
        var manifestBefore = await File.ReadAllBytesAsync(fixture.Paths.ManifestPath);
        var composeBefore = await File.ReadAllBytesAsync(Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml"));

        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => UpgradeForLimitsAsync(fixture));

        StringAssert.Contains(refused.Message, "instance manifest records", StringComparison.Ordinal);
        Assert.AreEqual(0, fixture.Runner.ComposeUpCount);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        CollectionAssert.AreEqual(manifestBefore, await File.ReadAllBytesAsync(fixture.Paths.ManifestPath));
        CollectionAssert.AreEqual(
            composeBefore, await File.ReadAllBytesAsync(Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml")));
        var journal = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(CameraAgentLifecycleManager.RefusedFailureCode, journal!.FailureCode);
        Assert.IsFalse(journal.MutationStarted);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ReinstallAsync_KeepsTheRecordedLimits()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Uninstalled);
        var pinned = new ContainerResourceLimits(4m, 8 * LimitGibibyte, 256, true, true);
        await RecordLimitsAsync(fixture, pinned);
        var composePath = Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml");
        var pinnedCompose = await File.ReadAllTextAsync(composePath);
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid,
            exists: false);

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Reinstall),
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(
            pinned,
            (await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None)).Resources);
        Assert.AreEqual(pinnedCompose, await File.ReadAllTextAsync(composePath));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ReadManifestAsync_LimitsThisInstallerCouldNotHaveRecorded_AreRefused()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.ManifestPath,
            fixture.Manifest with { Resources = new ContainerResourceLimits(1m, LimitGibibyte, 256, true, true) },
            DeploymentJsonContext.Default.InstanceManifest,
            CancellationToken.None);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None));

        StringAssert.Contains(exception.Message, "invalid or unsupported", StringComparison.Ordinal);
    }

    private static Task<LifecycleResult> UpgradeForLimitsAsync(LifecycleFixture fixture)
    {
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('a', 64)}";
        fixture.Runner.ConfigureRuntime(
            fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, $"sha256:{new string('b', 64)}", fixture.Uid, fixture.Gid);
        return CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                ImageReference = candidateReference,
                NoDownload = true,
                MigrationBackwardCompatible = true
            },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);
    }

    /// <summary>
    /// Records <paramref name="recorded"/> in the fixture's manifest and writes <paramref name="rendered"/>, by default
    /// the same limits, into its Compose file, keeping the authenticated model identity in step with the file.
    /// </summary>
    private static async Task RecordLimitsAsync(
        LifecycleFixture fixture,
        ContainerResourceLimits recorded,
        ContainerResourceLimits? rendered = null)
    {
        var composePath = Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml");
        var compose = CameraAgentContainerResources.WithLimits(
            await File.ReadAllTextAsync(composePath), rendered ?? recorded, recorded: null);
        await File.WriteAllTextAsync(composePath, compose);
        var model = ComposeDeployment.ComputeSha256(compose);
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.ManifestPath,
            manifest with { Resources = recorded, ComposeModelSha256 = model },
            DeploymentJsonContext.Default.InstanceManifest,
            CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.ResultPath,
            result with { ComposeModelSha256 = model },
            DeploymentJsonContext.Default.InstallationResult,
            CancellationToken.None);
    }
}
