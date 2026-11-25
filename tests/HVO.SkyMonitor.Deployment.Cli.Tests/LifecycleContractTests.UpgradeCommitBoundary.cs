using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    public async Task Rollback_CommitEndsCheckpointRecovery(bool plan, bool resume, bool laterJournal)
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        await UpgradeAndCommitAsync(fixture);
        var committed = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.IsNotNull(committed.PreviousStateSnapshot);
        if (laterJournal)
        {
            var backup = await CameraAgentLifecycleManager.BeginAsync(fixture.Request(LifecycleOperationKind.Backup),
                fixture.Paths, LifecycleOperationKind.Backup, committed, CancellationToken.None);
            await CameraAgentLifecycleManager.CompleteAsync(fixture.Paths, backup, CancellationToken.None);
        }

        var before = FileSystemActivity.Snapshot(fixture.Root);
        var dockerCalls = fixture.Runner.InvocationCount;
        var lifecycle = new FakeLifecycleClient();
        var refusal = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback) with { DryRun = plan, Resume = resume, AllowQuarantinedReadiness = true },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.IsTrue(refusal.IsRefusal);
        StringAssert.Contains(refusal.Message, "checkpoint recovery ends at upgrade commit", StringComparison.Ordinal);
        StringAssert.Contains(refusal.Message, "supported forward repair", StringComparison.Ordinal);
        StringAssert.Contains(refusal.Message, "Restore Only After an Interrupted Upgrade", StringComparison.Ordinal);
        Assert.AreEqual(dockerCalls, fixture.Runner.InvocationCount);
        Assert.AreEqual(0, lifecycle.PauseCount);
        FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), "post-commit rollback refusal");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(null, false)]
    [DataRow(null, true)]
    [DataRow("{", false)]
    [DataRow("[]", true)]
    [DataRow("{\"componentSchemaVersion\":12}", false)]
    public async Task Rollback_WithoutReadableLayoutDeclaration_GivesManualGuidance(string? manifestContents, bool plan)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        if (manifestContents is null)
            File.Delete(fixture.Paths.ManifestPath);
        else
            await File.WriteAllTextAsync(fixture.Paths.ManifestPath, manifestContents);
        var before = FileSystemActivity.Snapshot(fixture.Root);

        var refusal = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback) with { DryRun = plan },
            fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.IsTrue(refusal.IsRefusal);
        Assert.AreEqual(CameraAgentLifecycleManager.ManualRollbackGuidance, refusal.Message);
        Assert.AreEqual(0, fixture.Runner.InvocationCount);
        FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), "manual rollback refusal");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RollbackResume_PrecommitLegacyRollbackRetainsItsEvidenceForSupport()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, recoveryState: true);
        var request = fixture.Request(LifecycleOperationKind.Rollback);
        var operation = await CameraAgentLifecycleManager.BeginAsync(request, fixture.Paths,
            LifecycleOperationKind.Rollback, fixture.Manifest, CancellationToken.None);
        await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with { MutationStarted = true }, CancellationToken.None);
        var before = FileSystemActivity.Snapshot(fixture.Root);

        var refusal = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request with { Resume = true }, fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.IsTrue(refusal.IsRefusal);
        StringAssert.Contains(refusal.Message, "transaction-aware recovery", StringComparison.Ordinal);
        Assert.AreEqual(0, fixture.Runner.InvocationCount);
        FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), "legacy rollback resume refusal");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(0, "200", 200, true)]
    [DataRow(0, "503", 503, false)]
    [DataRow(0, "302", 302, false)]
    [DataRow(7, "000", null, null)]
    [DataRow(1, "200", null, null)]
    public async Task Status_ReportsApplicationReadinessSeparatelyFromAlive(
        int curlExit, string curlOutput, int? httpStatus, bool? ready)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.ApplicationHealth = new(curlExit, curlOutput, string.Empty);
        var result = await CameraAgentLifecycleManager.ExecuteAsync(fixture.Request(operation: null),
            fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("status", result.Outcome);
        Assert.IsTrue(result.Running);
        Assert.IsTrue(result.Alive);
        Assert.AreEqual(httpStatus, result.HealthStatusCode);
        Assert.AreEqual(ready, result.Healthy);
        Assert.AreEqual(1, fixture.Runner.ApplicationHealthProbes);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, DeploymentJsonContext.Default.LifecycleResult));
        Assert.IsTrue(json.RootElement.GetProperty("alive").GetBoolean());
        Assert.AreEqual(ready, json.RootElement.GetProperty("healthy").ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => (bool?)null
        });
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task Status_DriftedContainerCannotSupplyReadiness()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.Reboot(CandidateImageId, wasRunning: true);

        var result = await CameraAgentLifecycleManager.ExecuteAsync(fixture.Request(operation: null),
            fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("drifted", result.Outcome);
        Assert.IsFalse(result.Healthy);
        Assert.IsNull(result.HealthStatusCode);
        Assert.AreEqual(0, fixture.Runner.ApplicationHealthProbes);
    }
}
