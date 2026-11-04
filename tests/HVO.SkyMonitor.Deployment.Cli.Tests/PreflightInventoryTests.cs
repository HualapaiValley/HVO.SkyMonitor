using System.Text.Json.Nodes;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
[OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
public sealed class PreflightInventoryTests
{
    [TestMethod]
    [DataRow(".logicHost.publicEndpoint=\"http://127.0.0.1\"")]
    [DataRow(".logicHost.internalEndpoint=\"http://127.0.0.1:12345\"")]
    [DataRow(".logicHost.runtimeRoot=\"/srv/../private\"")]
    [DataRow(".logicHost.extra=\"rejected\"")]
    [DataRow(".images.tag=\"latest\"")]
    [DataRow(".images.logicHost.repository=\"https://user:password@registry.example/logichost\"")]
    [DataRow(".images.distributionMode=\"archive\" | .images.registryImmutableTags=false | .images.artifactRoot=\"relative/artifacts\"")]
    [DataRow(".images.registryImmutableTags=false")]
    [DataRow(".images.builder.endpoint=\"remote-context\"")]
    [DataRow(".cameraAgents[1].ownerPasswordSecretReference=.cameraAgents[0].ownerPasswordSecretReference")]
    [DataRow(".deployment.workload.profiles={W1:{path:\"/tmp/arbitrary-same-shape.json\",sha256:(\"a\"*64)}}")]
    [DataRow(".deployment.secretMappings += [{reference:\"SQL_PASSWORD\",key:\"ObjectStorage__AccessKey\"}]")]
    [DataRow(".catalogs[0].version=\"catalog:v1\"")]
    [DataRow(".catalogs[0].catalogId=\"Alternate\"")]
    [DataRow(".catalogs[0].catalogId=(\"a\"*33)")]
    public async Task InvalidShape_RejectsBeforeTransportAndMutation(string filter)
    {
        using var f = new PreflightFixture(); await f.StageAsync(); await Change(f, filter);
        var error = await Assert.ThrowsExactlyAsync<InstallerException>(() => f.RunAsync());
        StringAssert.Contains(error.Message, "schema-or-value-invalid", StringComparison.Ordinal); Assert.AreEqual(0, f.Opens); Assert.IsFalse(Directory.Exists(f.Host.StateRoot));
    }
    [TestMethod]
    [DataRow(".cameraAgents[1].instanceId=.cameraAgents[0].instanceId")]
    [DataRow(".cameraAgents[1].runtimeRoot=.cameraAgents[0].runtimeRoot")]
    [DataRow(".cameraAgents[1].catalogId=\"missing\"")]
    [DataRow(".catalogs[0].installRoot=.cameraAgents[0].runtimeRoot+\"/catalog\"")]
    [DataRow(".cameraAgents[0].expectedHostIdentity=.logicHost.expectedHostIdentity")]
    [DataRow(".cameraAgents[0].name=.logicHost.name")]
    [DataRow(".serviceEndpoints[0].fromTargets=[\"unknown\"]")]
    [DataRow(".images.builder.driver=\"docker\"")]
    [DataRow(".cameraAgents[0].ownerPasswordSecretReference=\"UNKNOWN\"")]
    public async Task InvalidCrossFieldsAndLayout_RejectBeforeTransport(string filter)
    {
        using var f = new PreflightFixture(); await f.StageAsync(); await Change(f, filter);
        await Assert.ThrowsExactlyAsync<InstallerException>(() => f.RunAsync()); Assert.AreEqual(0, f.Opens); Assert.IsFalse(Directory.Exists(f.Host.StateRoot));
    }
    [TestMethod]
    [DataRow("/srv/hvo", true)]
    [DataRow("/opt/hvo data/runtime", true)]
    [DataRow("/./srv", false)]
    [DataRow("/../srv", false)]
    [DataRow("/srv/./hvo", false)]
    [DataRow("/srv/../hvo", false)]
    [DataRow("/srv//hvo", false)]
    [DataRow("/srv/hvo\\root", false)]
    [DataRow("/srv/hvo,root", false)]
    [DataRow("/srv/hvo\"root", false)]
    [DataRow("/srv/hvo\tdata", false)]
    [DataRow("/srv/hvo\ndata", false)]
    public async Task RuntimeRootShape_MatchesPublishedSchema(string root, bool accepted)
    {
        using var f = new PreflightFixture(); await f.StageAsync(); f.Host.Inventory["logicHost"]!["runtimeRoot"] = root; DeploymentFakeHost.WriteJson(f.Host.InventoryPath, f.Host.Inventory);
        var read = () => SplitHostInventory.ReadAsync(f.Host.InventoryPath, "isolated", new ProcessRunner(), CancellationToken.None, validateLayout: false);
        if (accepted) await read(); else await Assert.ThrowsExactlyAsync<InstallerException>(read);
        var schema = Parse(DeploymentFakeHost.Read(Repository() + "/deploy/split-host/inventory.schema.json"));
        Assert.AreEqual(accepted, System.Text.RegularExpressions.Regex.IsMatch(root, schema["$defs"]!["runtimeRoot"]!.Text("pattern"), System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)));
    }
    [TestMethod]
    [DataRow("4.2", true)]
    [DataRow("hyg-v4.2-p3-s2-r1", true)]
    [DataRow("hyg-v42-fixture-1", true)]
    [DataRow("catalog:v1", false)]
    public async Task CatalogVersions_MatchPublishedSchema(string version, bool accepted)
    {
        using var f = new PreflightFixture(); await f.StageAsync(); f.Host.Inventory["catalogs"]![0]!["version"] = version; DeploymentFakeHost.WriteJson(f.Host.InventoryPath, f.Host.Inventory);
        if (accepted) await SplitHostInventory.ReadAsync(f.Host.InventoryPath, "isolated", new ProcessRunner(), CancellationToken.None); else await Assert.ThrowsExactlyAsync<InstallerException>(() => f.RunAsync());
        var schema = Parse(DeploymentFakeHost.Read(Repository() + "/deploy/split-host/inventory.schema.json"));
        Assert.AreEqual(accepted, System.Text.RegularExpressions.Regex.IsMatch(version, schema["$defs"]!["catalogInstallation"]!["properties"]!["version"]!.Text("pattern"), System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)));
    }
    [TestMethod]
    [DataRow("sql")]
    [DataRow("redis")]
    [DataRow("certificate")]
    [DataRow("owner")]
    [DataRow("bootstrap")]
    [DataRow("unused")]
    public async Task DuplicateSecrets_RejectGloballyBeforeTransport(string kind)
    {
        using var f = new PreflightFixture(); await f.StageAsync();
        var reference = kind switch { "sql" => "SQL_PASSWORD", "owner" => "EAST_PASSWORD", "bootstrap" => "OAUTH_SECRET", _ => "DUPLICATE_" + kind.ToUpperInvariant() };
        DeploymentFakeHost.Append(f.Host.Directory + "/secrets.env", reference + "=first\n" + reference + "=second\n");
        var error = await Assert.ThrowsExactlyAsync<InstallerException>(() => f.RunAsync()); StringAssert.Contains(error.Message, "duplicate-secret-reference", StringComparison.Ordinal); Assert.AreEqual(0, f.Opens);
    }
    [TestMethod]
    [DataRow("SQL_PASSWORD")]
    [DataRow("REDIS_PASSWORD")]
    [DataRow("OAUTH_SECRET")]
    [DataRow("CERTIFICATE_PASSWORD")]
    public async Task ControlSecretBytes_RejectBeforeRendering(string reference)
    {
        using var f = new PreflightFixture(); await f.StageAsync(); DeploymentFakeHost.Append(f.Host.Directory + "/secrets.env", reference + "=before\rafter\n");
        var error = await Assert.ThrowsExactlyAsync<InstallerException>(() => f.RunAsync()); StringAssert.Contains(error.Message, "control-character-rejected", StringComparison.Ordinal); Assert.AreEqual(0, f.Opens);
    }
    [TestMethod]
    [DataRow("mode")]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("control")]
    public async Task SecretSourceAndExtraction_ArePrivateUniqueAndNonEvaluating(string fault)
    {
        using var f = new PreflightFixture(); await f.StageAsync(); var path = f.Host.Directory + "/secrets.env";
        if (fault == "mode") File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        else if (fault == "missing") DeploymentFakeHost.Write(path, "EXACT=one\n");
        else if (fault == "duplicate") DeploymentFakeHost.Write(path, "EXACT=one\nEXACT=two\n");
        else DeploymentFakeHost.Write(path, "CONTROL=before\rafter\n");
        await Assert.ThrowsExactlyAsync<InstallerException>(() => f.RunAsync()); Assert.AreEqual(0, f.Opens); Assert.IsFalse(Directory.Exists(f.Host.StateRoot));
    }
    [TestMethod]
    [DataRow("logicHost")]
    [DataRow("east")]
    [DataRow("west")]
    [DataRow("proxy")]
    [DataRow("fixture")]
    public async Task PersistentMode_RequiresProductionHttpsAndProxy(string fault)
    {
        using var f = new PreflightFixture(); f.Host.Mode = "persistent"; await f.StageAsync();
        if (fault == "proxy") f.Host.Inventory["logicHost"]!["trustedProxyAddresses"] = new JsonArray();
        else if (fault == "fixture") f.Host.Inventory["catalogs"]![0]!["kind"] = "fixture";
        else { var node = fault == "logicHost" ? f.Host.Inventory["logicHost"]! : f.Host.Inventory.Array("cameraAgents").Single(n => n!.Text("name") == fault)!; node["publicEndpoint"] = node.Text("publicEndpoint").Replace("https:", "http:", StringComparison.Ordinal); }
        DeploymentFakeHost.WriteJson(f.Host.InventoryPath, f.Host.Inventory); await Assert.ThrowsExactlyAsync<InstallerException>(() => f.RunAsync()); Assert.AreEqual(0, f.Opens);
    }
    internal static async Task Change(PreflightFixture fixture, string filter)
    {
        var result = await new ProcessRunner().RunAsync("jq", ["-c", filter, fixture.Host.InventoryPath], CancellationToken.None); Assert.AreEqual(0, result.ExitCode);
        DeploymentFakeHost.Write(fixture.Host.InventoryPath, result.StandardOutput);
    }
    internal static string Repository()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null && !File.Exists(directory.FullName + "/HVO.SkyMonitor.v9.slnx")) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository required.");
    }
}
