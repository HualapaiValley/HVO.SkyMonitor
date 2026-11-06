using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task Review1460_PureSwapAtOldSafeBudget()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed, installedImage: CurrentStateContract);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);
        var identity = CameraAgentStateLayout.IdentityDatabasePath(fixture.Paths.StateRoot);
        ExecuteSql(identity,
            "CREATE TABLE __EFMigrationsHistory(MigrationId TEXT);" +
            "INSERT INTO __EFMigrationsHistory VALUES('20260827053715_InitialIdentity');");
        var availableBefore = LinuxHostEnvironment.AvailableBytes(fixture.Paths.ProductRoot);
        // Only length is needed by admission. No large allocation or disk filling is involved.
        using (var stream = new FileStream(identity, FileMode.Open, FileAccess.Write))
            stream.SetLength(availableBefore / 3 / 4096 * 4096);
        var bytes = new FileInfo(identity).Length;
        var available = LinuxHostEnvironment.AvailableBytes(fixture.Paths.ProductRoot);
        var oldRequired = 2 * bytes + 256L * 1024 * 1024;
        var newRequired = 4 * bytes + CameraAgentStateCounters.JournalRecordBytes(bytes) + 256L * 1024 * 1024;
        Console.WriteLine($"D={bytes}; free={available}; oldRequired={oldRequired}; newRequired={newRequired}");
        Assert.IsTrue(oldRequired < available);
        Assert.IsTrue(newRequired > available);
        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", result.Outcome);
        var operation = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.IsFalse(operation!.StateSnapshotRequired);
        Assert.IsNull(operation.StateSnapshot);
    }
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Review1460_ReasonContainingKeyIsTerminalSafe(bool json)
    {
        using var fixture = await MigratingFixtureAsync();
        var target = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, AutomationStore);
        ExecuteSql(target, FixtureDump(AutomationStore));
        const string key = "agent\u001b[31m\"";
        SaveDefinition(target, key, 1);
        ExecuteSql(target, "UPDATE automation_definition_revisions SET definition_json='{}';" +
            "CREATE TRIGGER refuse_update BEFORE UPDATE ON automation_definitions BEGIN SELECT RAISE(IGNORE); END;");
        StateCounterFloor[] floors = [new(AutomationStore, "automation-definition-version", key, 2)];
        var plan = CameraAgentStateCounters.PlanCarry(floors, _ => target, Path.Combine(fixture.Root, "reason-trial"));
        Assert.HasCount(1, plan.Uncarried);
        using var error = new StringWriter();
        await CameraAgentStateSnapshot.ShowRestoreUncarriedAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), DateTimeOffset.UtcNow), plan.Uncarried, json, error);
        var warning = error.ToString();
        if (json)
        {
            using var document = JsonDocument.Parse(warning);
            warning = document.RootElement.GetProperty("warning").GetString()!;
        }
        Console.WriteLine(JsonSerializer.Serialize(warning));
        Assert.IsFalse(warning.Contains(key, StringComparison.Ordinal), "The reason echoed the raw counter key.");
        Assert.IsFalse(warning.Contains('\u001b', StringComparison.Ordinal), "The warning contains a terminal escape character.");
        // The reason's echo of the key loses its decoration, which redaction removes first (#1491).
        StringAssert.Contains(warning,
            JsonEncodedText.Encode($"The restored '{AutomationStore}' cannot continue the version line of automation definition 'agent\"'.").ToString(),
            StringComparison.Ordinal);
        using var redactedError = new StringWriter();
        await CameraAgentStateSnapshot.ShowRestoreUncarriedAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), DateTimeOffset.UtcNow),
            plan.Uncarried.Select(static entry => (entry.What, entry.Reason + " token=carry-redaction-sentinel")).ToArray(),
            json, redactedError);
        Assert.IsFalse(redactedError.ToString().Contains("carry-redaction-sentinel", StringComparison.Ordinal),
            "Escaping a key must retain diagnostic redaction.");
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task Review1460_StatefulUpgradeShortOfJournal_IsRefusedBeforeMutation()
    {
        using var fixture = await MigratingFixtureAsync();
        var identity = CameraAgentStateLayout.IdentityDatabasePath(fixture.Paths.StateRoot);
        ExecuteSql(identity,
            "CREATE TABLE __EFMigrationsHistory(MigrationId TEXT);" +
            "INSERT INTO __EFMigrationsHistory VALUES('20260827053715_InitialIdentity');");
        var databaseBytes = CameraAgentStateSnapshot.StoreBytes(fixture.Paths.StateRoot,
            CameraAgentStateInventory.Stores(fixture.Paths, fixture.Manifest.ReplayProfile));
        var available = 4 * databaseBytes + 256L * 1024 * 1024;
        Assert.IsTrue(CameraAgentStateCounters.JournalRecordBytes(databaseBytes) > 0);
        var lifecycle = new FakeLifecycleClient();
        CameraAgentLifecycleManager.UpgradeStorageHostOverride.Value =
            new FakeHostEnvironment { Fallback = new(fixture.Paths.ProductRoot, "test", available) };
        try
        {
            var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => lifecycle,
                _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None));
            StringAssert.Contains(refused.Message, "Insufficient free space", StringComparison.Ordinal);
        }
        finally
        {
            CameraAgentLifecycleManager.UpgradeStorageHostOverride.Value = null;
        }
        Assert.AreEqual(0, lifecycle.PauseCount);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        var operation = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.IsFalse(operation!.MutationStarted);
        Assert.IsNull(operation.StateSnapshot);
    }

}
