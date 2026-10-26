using System.Text.Json.Nodes;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
[OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
public sealed class PreflightLayoutTests
{
    private const string Shared = """
        .sharedServices={name:"shared",sshHost:"shared@example",dockerContext:"shared-context",expectedArchitecture:"amd64",
        expectedHostName:"shared-node",expectedHostIdentity:"shared-machine",expectedDockerDaemonIdentity:"shared-daemon",runtimeRoot:"/srv/hvo/shared",
        runtimeOwner:.logicHost.runtimeOwner,ports:[1433,6379,2525,8025]} |
        .deployment.services.mode="deploy" | .deployment.services.smtp.kind="mailpit" | .deployment.services.smtp.ports=[2525,8025] |
        .deployment.services.images={sqlServer:("registry.example/sql@sha256:"+("a"*64)),redis:("registry.example/redis@sha256:"+("b"*64)),mailpit:("registry.example/mailpit@sha256:"+("e"*64))} |
        .deployment.limits.sqlMemory="4G" | .deployment.limits.sqlMemoryLimitMb=3072
        """;
    [TestMethod]
    [DataRow(".", true)]
    [DataRow(".sharedServices.expectedHostName=.logicHost.expectedHostName | .sharedServices.expectedHostIdentity=.logicHost.expectedHostIdentity | .sharedServices.expectedDockerDaemonIdentity=.logicHost.expectedDockerDaemonIdentity", true)]
    [DataRow("del(.sharedServices.sshHost)", false)]
    [DataRow(".sharedServices.ports=[1433,6379,9000,2525]", false)]
    [DataRow(".deployment.services.redis.port=.deployment.services.sql.port | .sharedServices.ports=[1433,9000,2525,8025]", false)]
    [DataRow(".deployment.limits.sqlMemory=null", false)]
    [DataRow(".deployment.limits.sqlMemoryLimitMb=4096", false)]
    [DataRow(".deployment.limits.sqlMemory=\"010G\"", false)]
    public async Task SharedServicesShapeAndSqlLimits_PreserveEveryCurrentVector(string filter, bool valid)
    {
        using var f = new PreflightFixture(); await f.StageAsync(); await PreflightInventoryTests.Change(f, Shared + " | " + filter);
        var read = () => SplitHostInventory.ReadAsync(f.Host.InventoryPath, "isolated", new ProcessRunner(), CancellationToken.None);
        if (valid) await read(); else await Assert.ThrowsExactlyAsync<InstallerException>(read);
    }
    [TestMethod]
    public async Task SqlMemory_IsCheckedAgainstTheSharedTargetObservation()
    {
        using var f = new PreflightFixture(); await f.StageAsync(); await PreflightInventoryTests.Change(f, Shared + " | .deployment.limits.sqlMemory=\"9G\" | .deployment.limits.sqlMemoryLimitMb=8192");
        // Refresh only the disposable fake's immutable target bindings; the production phase reads the changed inventory itself.
        var document = Parse(DeploymentFakeHost.Read(f.Host.InventoryPath));
        f.Host.Inventory["sharedServices"] = document["sharedServices"]!.DeepClone(); f.Host.Inventory["sharedServices"]!["runtimeRoot"] = f.Host.LogicalRoot + "/remote/shared"; f.Host.Inventory["deployment"] = document["deployment"]!.DeepClone();
        await f.Host.StageAsync(); Directory.Delete(f.Host.StateRoot, true); Directory.Delete(f.Host.EvidenceRoot, true);
        await Assert.ThrowsExactlyAsync<InstallerException>(() => f.RunAsync());
        var failed = f.Manifest.Array("checks").Single(n => n!.Text("status") == "failed")!;
        Assert.AreEqual("shared", failed.Text("target")); Assert.AreEqual("host-capacity", failed.Text("check")); Assert.AreEqual("below-sql-memory-limit", failed.Text("reason"));
    }
    [TestMethod]
    public async Task CatalogSelectionAndFriendlyRename_PreservePersistentIdentityAndProject()
    {
        using var f = new PreflightFixture(); await f.StageAsync();
        await PreflightInventoryTests.Change(f, ".catalogs += [(.catalogs[0] | .catalogId=\"alternate\" | .installRoot=(.installRoot | sub(\"test-production$\";\"alternate\")))] | .cameraAgents[1].catalogId=\"alternate\"");
        var before = await SplitHostInventory.ReadAsync(f.Host.InventoryPath, "isolated", new ProcessRunner(), CancellationToken.None);
        var east = before.Targets[1]; var westNode = before.Document["cameraAgents"]![1]!;
        Assert.AreEqual(before.Document.Text("productRoot") + "/catalogs/test-production", before.Document.Array("catalogs").Single(n => n!.Text("catalogId") == "test-production")!.Text("installRoot"));
        Assert.AreEqual("alternate", westNode.Text("catalogId"));
        Assert.AreEqual(before.Document.Text("productRoot") + "/catalogs/alternate", before.Document.Array("catalogs").Single(n => n!.Text("catalogId") == westNode.Text("catalogId"))!.Text("installRoot"));
        await PreflightInventoryTests.Change(f, ".cameraAgents[0].friendlyName=\"Renamed camera\"");
        var after = await SplitHostInventory.ReadAsync(f.Host.InventoryPath, "isolated", new ProcessRunner(), CancellationToken.None);
        Assert.AreEqual(east, after.Targets[1]); Assert.AreEqual(before.Project(east), after.Project(after.Targets[1])); Assert.AreEqual(before.Installation, after.Installation);
    }
    [TestMethod]
    [DataRow("/var/lib/hvo-sky", true)]
    [DataRow("/opt/not-skymonitor", false)]
    public async Task PersistentProductRoot_IsCanonical(string root, bool valid)
    {
        using var f = new PreflightFixture(); f.Host.Mode = "persistent"; await f.StageAsync();
        f.Host.SetProductRoot(root); DeploymentFakeHost.WriteJson(f.Host.InventoryPath, f.Host.Inventory);
        if (valid) await SplitHostInventory.ReadAsync(f.Host.InventoryPath, "persistent", new ProcessRunner(), CancellationToken.None);
        else await Assert.ThrowsExactlyAsync<InstallerException>(() => f.RunAsync());
    }
    [TestMethod]
    [DataRow("hvo.main.artifacts")]
    [DataRow("abcdefghijklmnopqrstuvwxyz0123456789-artifacts")]
    public async Task BucketNamesAndModulePaths_KeepTheirExistingLessRestrictiveShape(string bucket)
    {
        using var f = new PreflightFixture(); await f.StageAsync();
        await PreflightInventoryTests.Change(f, ".cameraAgents[0].moduleConfigPath=\"/tmp/module,config\\\\\\\".json\" | .deployment.services.objectStore.artifactBucket=\"" + bucket + "\" | .deployment.resources.artifactBucket=\"" + bucket + "\"");
        await SplitHostInventory.ReadAsync(f.Host.InventoryPath, "isolated", new ProcessRunner(), CancellationToken.None);
    }
}
