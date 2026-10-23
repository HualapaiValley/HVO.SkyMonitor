using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment.Contracts;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
[OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
public sealed class PreflightTests
{
    private static byte[] ReadBytes(string path) => File.ReadAllBytes(path);
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task ValidSplitSameHostAndQuotedPaths_PreserveWireContractAndReadOnlyTargets(bool sameHost, bool spaced)
    {
        using var f = new PreflightFixture();
        if (spaced) f.Host.SetProductRoot(f.Host.LogicalRoot + "/remote/east space/skymonitor");
        if (sameHost) foreach (var camera in f.Host.Inventory.Array("cameraAgents")) foreach (var key in new[] { "sshHost", "dockerContext", "expectedArchitecture", "expectedHostName", "expectedHostIdentity", "expectedDockerDaemonIdentity" }) camera![key] = f.Host.Inventory["logicHost"]![key]!.DeepClone();
        await f.StageAsync(); var manifest = await f.RunAsync();
        Assert.AreEqual("passed", manifest.Text("phaseStatus")); Assert.AreEqual(3, manifest.Array("targets").Count);
        Assert.AreEqual(35, manifest.Array("checks").Count);
        Assert.IsTrue(manifest.Array("targets").All(n => n!.Text("dockerComposeVersion") == "v2.39.1" && n.Text("deploymentState") == "needs_prepare"));
        Assert.IsFalse(f.Operations.Any(op => op is "write" or "mkdir" or "unlink" or "move"));
        Assert.IsFalse(f.Operations.Contains("http-reachable", StringComparer.Ordinal));
        Assert.IsTrue(manifest.Array("checks").Where(n => n!.Text("check") == "logic-public-authority").All(n => n!.Text("reason") == "deferred-until-up"));
        Assert.AreEqual(SafeFileSystem.OwnerDirectoryMode, File.GetUnixFileMode(f.Host.StateRoot));
        foreach (var path in new[] { f.Host.StateRoot + "/manifest.json", f.Host.EvidenceRoot + "/preflight.json" }) Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.IsFalse(manifest.ToJsonString().Contains("fixture-secret-never-print", StringComparison.Ordinal));
        Assert.IsTrue(JsonNode.DeepEquals(SplitHostPreflight.Evidence(manifest), f.Evidence));
        await f.RunAsync();
    }
    [TestMethod]
    [DataRow("host-id", "host-identity", "mismatch")]
    [DataRow("host-name", "host-name", "mismatch")]
    [DataRow("facts", "ssh", "invalid-response")]
    [DataRow("host-architecture", "host-architecture", "mismatch")]
    [DataRow("unreachable", "ssh", "unreachable-or-invalid-response")]
    [DataRow("missing-bash", "ssh", "tool-unavailable")]
    [DataRow("missing-ss", "ports", "inspector-unavailable")]
    [DataRow("host-port", "ports", "conflict")]
    [DataRow("context-unavailable", "docker-context", "unavailable")]
    [DataRow("context-mismatch", "docker-context", "mismatch")]
    [DataRow("daemon-unreachable", "docker-daemon", "unreachable")]
    [DataRow("daemon-id", "ssh-docker-correlation", "mismatch")]
    [DataRow("daemon-name", "ssh-docker-correlation", "mismatch")]
    [DataRow("architecture", "docker-daemon", "invalid-response")]
    [DataRow("os", "docker-daemon", "invalid-response")]
    [DataRow("version", "docker-daemon", "invalid-response")]
    [DataRow("docker-json", "docker-daemon", "invalid-response")]
    [DataRow("unsupported-architecture", "docker-daemon", "unsupported-architecture")]
    [DataRow("windows", "ssh-docker-correlation", "mismatch")]
    [DataRow("compose-missing", "docker-compose", "unavailable")]
    [DataRow("compose-version", "docker-compose", "invalid-version")]
    [DataRow("containers-query", "containers", "query-failed")]
    [DataRow("docker-port", "containers", "port-conflict")]
    [DataRow("tcp", "test-service", "unreachable")]
    [DataRow("missing-timeout", "test-service", "tool-unavailable")]
    public async Task UnsafeHostProbe_RecordsExactBoundedFailure(string fault, string check, string reason)
    {
        using var f = new PreflightFixture(); await f.StageAsync(); f.Fault = fault;
        await Assert.ThrowsExactlyAsync<InstallerException>(() => f.RunAsync());
        var connectivity = fault is "tcp" or "missing-timeout";
        Assert.AreEqual(connectivity ? "running" : "failed", f.Manifest.Text("phaseStatus"));
        if (connectivity) Assert.IsFalse(File.Exists(f.Host.EvidenceRoot + "/preflight.json")); else Assert.AreEqual("failed", f.Evidence.Text("phaseStatus"));
        var failed = f.Manifest.Array("checks").Single(n => n!.Text("status") == "failed")!;
        Assert.AreEqual(check, failed.Text("check")); Assert.AreEqual(reason, failed.Text("reason"));
        Assert.IsFalse(f.Status.Any(s => s.Contains("ppc64le", StringComparison.Ordinal) || s.Contains("private diagnostic", StringComparison.Ordinal)));
    }
    [TestMethod]
    [DataRow("", true)]
    [DataRow("http", false)]
    [DataRow("missing-curl", false)]
    public async Task PersistentAuthority_IsProbedAndNeverDeferred(string fault, bool success)
    {
        using var f = new PreflightFixture(); f.Host.Mode = "persistent"; await f.StageAsync(); f.Fault = fault;
        if (success) await f.RunAsync(); else await Assert.ThrowsExactlyAsync<InstallerException>(() => f.RunAsync());
        Assert.AreEqual(success ? "passed" : "running", f.Manifest.Text("phaseStatus")); Assert.IsTrue(f.Operations.Contains("http-reachable", StringComparer.Ordinal));
        if (!success) Assert.IsFalse(File.Exists(f.Host.EvidenceRoot + "/preflight.json"));
    }
    [TestMethod]
    public async Task FailedRerun_ReplacesPassedEvidence_WhileRejectedInputsPreserveIt()
    {
        using var f = new PreflightFixture(); await f.StageAsync(); await f.RunAsync(); f.Fault = "unreachable";
        await Assert.ThrowsExactlyAsync<InstallerException>(() => f.RunAsync()); Assert.AreEqual("failed", f.Evidence.Text("phaseStatus"));
        f.Fault = ""; await f.RunAsync(); var before = ReadBytes(f.Host.EvidenceRoot + "/preflight.json");
        f.Host.Inventory["unexpected"] = "invalid"; DeploymentFakeHost.WriteJson(f.Host.InventoryPath, f.Host.Inventory);
        await Assert.ThrowsExactlyAsync<InstallerException>(() => f.RunAsync()); CollectionAssert.AreEqual(before, ReadBytes(f.Host.EvidenceRoot + "/preflight.json"));
    }
    [TestMethod]
    [DataRow("check")]
    [DataRow("target")]
    [DataRow("evidence-symlink")]
    [DataRow("corrupt-manifest")]
    [DataRow("inventory-hash")]
    public async Task RejectedResume_DoesNotRewritePriorStateOrEvidence(string fault)
    {
        using var f = new PreflightFixture(); await f.StageAsync(); await f.RunAsync(); var path = f.Host.StateRoot + "/manifest.json";
        var sentinel = f.Host.Directory + "/do-not-touch"; DeploymentFakeHost.Write(sentinel, "preserve");
        if (fault == "check") { var doc = f.Manifest; doc["checks"]![0]!["status"] = "failed"; DeploymentFakeHost.WriteJson(path, doc); }
        if (fault == "target") { var doc = f.Manifest; doc["targets"]![0]!["hostIdentity"] = "tampered"; DeploymentFakeHost.WriteJson(path, doc); }
        if (fault == "corrupt-manifest") DeploymentFakeHost.Write(path, "not-json\n");
        if (fault == "evidence-symlink") { File.Delete(f.Host.EvidenceRoot + "/preflight.json"); File.CreateSymbolicLink(f.Host.EvidenceRoot + "/preflight.json", sentinel); }
        if (fault == "inventory-hash") { f.Host.Inventory["catalogs"]![0]!["version"] = "catalog-v2"; DeploymentFakeHost.WriteJson(f.Host.InventoryPath, f.Host.Inventory); }
        var before = ReadBytes(path); var evidence = ReadBytes(f.Host.EvidenceRoot + "/preflight.json"); var opens = f.Opens;
        await Assert.ThrowsExactlyAsync<InstallerException>(() => f.RunAsync());
        CollectionAssert.AreEqual(before, ReadBytes(path)); CollectionAssert.AreEqual(evidence, ReadBytes(f.Host.EvidenceRoot + "/preflight.json"));
        Assert.AreEqual(opens, f.Opens); Assert.AreEqual("preserve", DeploymentFakeHost.Read(sentinel));
    }
    [TestMethod]
    public async Task AbruptEvidenceBoundaryAndCancellation_RetainTruthfulResumableState()
    {
        using var f = new PreflightFixture(); await f.StageAsync();
        await Assert.ThrowsExactlyAsync<PartialPrepareCrashException>(() => f.RunAsync(name => { if (name == "after-evidence-before-passed-manifest") throw new PartialPrepareCrashException(); }));
        Assert.AreEqual("running", f.Manifest.Text("phaseStatus")); Assert.AreEqual("passed", f.Evidence.Text("phaseStatus")); await f.RunAsync();
        using var cancel = new CancellationTokenSource(); f.Host.BeforePrimitive = (_, _) => cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.RunAsync(token: cancel.Token)); Assert.AreEqual("failed", f.Evidence.Text("phaseStatus"));
    }
    [TestMethod]
    public async Task ManifestRenameFailureBeforeAdmission_PreservesPriorPassedEvidence()
    {
        using var f = new PreflightFixture(); await f.StageAsync(); await f.RunAsync();
        var manifest = ReadBytes(f.Host.StateRoot + "/manifest.json"); var evidence = ReadBytes(f.Host.EvidenceRoot + "/preflight.json"); var opens = f.Opens;
        await Assert.ThrowsExactlyAsync<InstallerException>(() => f.RunAsync(name => { if (name == "before-manifest-rename") throw Error("atomic-publication-failed"); }));
        CollectionAssert.AreEqual(manifest, ReadBytes(f.Host.StateRoot + "/manifest.json")); CollectionAssert.AreEqual(evidence, ReadBytes(f.Host.EvidenceRoot + "/preflight.json")); Assert.AreEqual(opens, f.Opens);
    }
    [TestMethod]
    [DataRow("4096M", "4194304")]
    [DataRow("4G", "4194304")]
    [DataRow("08G", null)]
    public void MemoryLimit_UsesExistingUnitsAndRejectsLeadingZero(string value, string? expected)
        => Assert.AreEqual(expected, PreflightHostObservation.MemoryToKiB(value)?.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
