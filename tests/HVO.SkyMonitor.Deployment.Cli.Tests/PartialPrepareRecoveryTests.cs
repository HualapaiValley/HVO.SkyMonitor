using System.Text.Json.Nodes;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class PartialPrepareRecoveryTests
{
    private static readonly string[] ExpectedDispositions = ["cleanup-candidate", "not-created", "not-created"];
    [TestMethod]
    public async Task InspectAndCleanup_UsesRealTypedPhase_AndResumesEachDeletionBoundary()
    {
        using var host = new DeploymentFakeHost(); await host.StageAsync();
        var root = host.Physical(host.Prepared[0].RuntimeRoot);
        var unrelated = host.Directory + "/unrelated-resource"; DeploymentFakeHost.Write(unrelated, "preserve\n");
        var inspect = await host.RunAsync(inspect: true);
        Assert.AreEqual("passed", inspect.Text("phaseStatus")); Assert.AreEqual("dry-run", inspect.Text("operation")); Assert.AreEqual(0, inspect.Array("resources").Count);
        CollectionAssert.AreEqual(ExpectedDispositions, inspect.Array("targets").Select(t => t!.Text("disposition")).ToArray());
        Assert.IsFalse(inspect.ToJsonString().Contains(host.Directory, StringComparison.Ordinal)); Assert.IsFalse(inspect.ToJsonString().Contains(host.LogicalRoot, StringComparison.Ordinal));
        Assert.IsTrue(Directory.Exists(root));
        foreach (var point in new[] { "marker", "control", "root", "lock-state", "lock" })
        {
            await Assert.ThrowsExactlyAsync<InstallerException>(() => host.RunAsync(boundary: name => { if (name == "after-partial-prepare-" + point + ":logic") throw Error("injected-failure"); }));
            var failed = Parse(DeploymentFakeHost.Read(host.StateRoot + "/partial-prepare-cleanup-ledger.json"));
            Assert.AreEqual("failed", failed.Text("phaseStatus")); Assert.IsTrue(failed.Array("resources").Any(r => r!.Text("status") is "intent" or "completed"));
        }
        Assert.IsFalse(Directory.Exists(root));
        var result = await host.RunAsync(); await host.RunAsync();
        Assert.AreEqual("passed", result.Text("phaseStatus")); Assert.AreEqual("cleanup", result.Text("operation")); Assert.AreEqual(2, result.Array("resources").Count(r => r!.Text("status") == "completed"));
        Assert.AreEqual("preserve\n", DeploymentFakeHost.Read(unrelated));
        foreach (var file in Directory.EnumerateFiles(host.StateRoot).Concat(Directory.EnumerateFiles(host.EvidenceRoot))) Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }
    [TestMethod]
    [DataRow("hash")][DataRow("installation")][DataRow("target")][DataRow("root")][DataRow("owner")]
    public async Task ChangedInventory_IsRejectedBeforeMutation(string mutation)
    {
        using var host = new DeploymentFakeHost(); await host.StageAsync();
        switch (mutation)
        {
            case "hash": host.Inventory["catalogs"]![0]!["displayName"] = "changed"; break;
            case "installation": host.Inventory["installationId"] = "changed-installation"; break;
            case "target": host.Inventory["logicHost"]!["expectedHostIdentity"] = "changed-machine"; break;
            case "root": host.SetProductRoot(host.LogicalRoot + "/changed-skymonitor"); break;
            case "owner": host.Inventory["logicHost"]!["runtimeOwner"] = "missing-owner"; break;
        }
        DeploymentFakeHost.WriteJson(host.InventoryPath, host.Inventory);
        await Reject(host, "mismatch-or-not-passed");
    }
    [TestMethod]
    [DataRow("host")][DataRow("daemon")][DataRow("unjournaled")][DataRow("marker")][DataRow("lock")][DataRow("state")][DataRow("application")][DataRow("foreign-parent")][DataRow("mount")]
    public async Task InvalidRemoteProvenance_IsRejectedBeforeMutation(string mutation)
    {
        using var host = new DeploymentFakeHost(); await host.StageAsync(); var prepared = host.Prepared[0]; var root = host.Physical(prepared.RuntimeRoot);
        var lockPath = host.Physical(Path.GetDirectoryName(prepared.RuntimeRoot) + "/" + SplitHostInventory.LockName(host.Contract.Targets[0]));
        switch (mutation)
        {
            case "host": host.BadHost = "logic"; break;
            case "foreign-parent": host.PrimitiveResponse = (operation, args, result) => operation == "stat" && args[0] == Path.GetDirectoryName(prepared.RuntimeRoot) ? "4294967294" + result[result.IndexOf(' ')..] : result; break;
            case "mount": host.PrimitiveResponse = (operation, _, result) => operation == "mounts" ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("1 0 0:0 / " + prepared.RuntimeRoot + " rw - ext4 /dev/fake rw\n")) : result; break;
            case "daemon": host.BadDaemon = "logic"; break;
            case "unjournaled": Directory.CreateDirectory(host.Physical(host.Contract.Targets[1].RuntimeRoot)); break;
            case "marker": DeploymentFakeHost.Write(root + "/.hvo-deploy/ownership", "tampered\n"); break;
            case "lock": DeploymentFakeHost.Write(lockPath, "tampered\n"); break;
            case "state": DeploymentFakeHost.Write(lockPath + ".state", PartialPrepareTarget.State(prepared).Replace("rootNew\ttrue", "rootNew\tfalse", StringComparison.Ordinal)); break;
            case "application": DeploymentFakeHost.Write(root + "/application-state", "must-not-delete\n"); break;
        }
        await Reject(host, mutation == "daemon" ? "docker-daemon-correlation-mismatch" : mutation == "unjournaled" ? "unjournaled-prepare-artifacts" : "provenance-or-content-invalid");
    }
    [TestMethod]
    [DataRow("container")][DataRow("network")][DataRow("volume")][DataRow("named-container")][DataRow("named-network")][DataRow("late")]
    public async Task DeploymentResources_BlockAllDeletion_IncludingAfterIntent(string kind)
    {
        using var host = new DeploymentFakeHost(); await host.StageAsync();
        var target = host.Contract.Targets[0];
        if (kind == "named-container") host.PresentContainer = "hvo-skymonitor-" + target.InstanceId!.Replace("-", "", StringComparison.Ordinal);
        else if (kind == "named-network") host.PresentNetwork = host.Contract.Project(target) + "_default";
        else
        {
            host.Resource = "logic-context:" + (kind == "late" ? "container" : kind) + ":" + (kind == "late" ? "label=io.hvoskymonitor.run-id=" + host.RunId : "label=com.docker.compose.project=" + host.Contract.Project(target));
            host.ResourceAfter = kind == "late" ? 2 : 1;
        }
        await Reject(host, "unexpected-deployment-resource", inspect: kind != "late");
        Assert.IsTrue(File.Exists(host.Physical(target.RuntimeRoot) + "/.hvo-deploy/ownership"));
    }
    [TestMethod]
    public async Task ResourceChecks_AlwaysUsePinnedEndpoint()
    {
        using var host = new DeploymentFakeHost(); await host.StageAsync(); await host.RunAsync(inspect: true);
        Assert.IsTrue(host.DockerCalls.Any(c => c[0] == "context"));
        Assert.IsTrue(host.DockerCalls.Where(c => c[0] != "context").All(c => c[0] == "--host" && c[1].StartsWith("ssh://", StringComparison.Ordinal)));
    }
    [TestMethod]
    [DataRow("ownership", false)][DataRow("lock-state", false)][DataRow("ownership", true)]
    public async Task ByteIdenticalReplacement_IsPreservedAndRejected(string artifact, bool onMove)
    {
        using var host = new DeploymentFakeHost(); await host.StageAsync(); var target = host.Contract.Targets[0];
        var path = artifact == "ownership" ? target.RuntimeRoot + "/.hvo-deploy/ownership" : Path.GetDirectoryName(target.RuntimeRoot) + "/" + SplitHostInventory.LockName(target) + ".state";
        var physical = host.Physical(path); var expected = File.ReadAllBytes(physical); var fired = false; var seen = 0;
        host.BeforePrimitive = (operation, args) =>
        {
            if (fired) return;
            var source = operation == "move" ? args[0] : "";
            var actual = source.StartsWith("/proc/", StringComparison.Ordinal) ? new FileInfo(source).FullName : source;
            if (onMove ? operation == "move" && Path.GetFileName(actual) == "ownership" : operation == "stat" && args[0] == path && ++seen == 5)
            {
                fired = true; File.Move(physical, physical + ".race-original"); File.WriteAllBytes(physical, expected); File.SetUnixFileMode(physical, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        };
        await Assert.ThrowsExactlyAsync<InstallerException>(() => host.RunAsync());
        Assert.IsTrue(fired); CollectionAssert.AreEqual(expected, File.ReadAllBytes(physical)); Assert.IsTrue(File.Exists(physical + ".race-original"));
    }
    [TestMethod]
    public async Task PreexistingRoot_IsRetained_AndForeignUnpreparedTargetIsUntouched()
    {
        using var host = new DeploymentFakeHost { Mode = "persistent" }; await host.StageAsync(preexisting: true);
        await host.RunAsync(); var root = host.Physical(host.Prepared[0].RuntimeRoot); Assert.IsTrue(Directory.Exists(root)); Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(root).Count());
        var result = Parse(DeploymentFakeHost.Read(host.EvidenceRoot + "/partial-prepare-cleanup.json"));
        Assert.IsFalse(result.Array("targets")[0]!["newlyCreated"]!.Flag("root"));
    }
    [TestMethod]
    public async Task UnpreparedReadyRoot_WithForeignParent_IsRetained()
    {
        using var host = new DeploymentFakeHost(); await host.StageAsync(); var root = host.Physical(host.Contract.Targets[1].RuntimeRoot);
        Directory.CreateDirectory(root); DeploymentFakeHost.Write(root + "/pre-existing-state", "preserve\n");
        foreach (var path in new[] { host.StateRoot + "/manifest.json", host.EvidenceRoot + "/preflight.json" })
        {
            var value = Parse(DeploymentFakeHost.Read(path)); value.Array("targets")[1]!["deploymentState"] = "ready"; DeploymentFakeHost.WriteJson(path, value);
        }
        var parent = Path.GetDirectoryName(host.Contract.Targets[1].RuntimeRoot);
        host.PrimitiveResponse = (operation, args, result) => operation == "stat" && args[0] == parent ? "4294967294" + result[result.IndexOf(' ')..] : result;
        await host.RunAsync(); Assert.AreEqual("preserve\n", DeploymentFakeHost.Read(root + "/pre-existing-state"));
    }
    [TestMethod]
    [DataRow("lock")][DataRow("duplicate")][DataRow("creating-run")]
    public async Task RetainedPersistentProvenance_RequiresExactCanonicalBytes(string mutation)
    {
        using var host = new DeploymentFakeHost { Mode = "persistent" }; await host.StageAsync(foreign: true);
        var lockPath = host.Physical(Path.GetDirectoryName(host.Prepared[0].RuntimeRoot) + "/" + SplitHostInventory.LockName(host.Contract.Targets[0]));
        if (mutation == "lock") DeploymentFakeHost.Append(lockPath, "\n");
        else if (mutation == "duplicate") DeploymentFakeHost.Append(lockPath + ".state", "rootNew\ttrue\n");
        else DeploymentFakeHost.Write(lockPath + ".state", DeploymentFakeHost.Read(lockPath + ".state").Replace("partial-foreign-old", "partial-foreign-other", StringComparison.Ordinal));
        await Reject(host, "preserved-provenance-invalid");
    }
    [TestMethod]
    public async Task RetainedPersistentTarget_IsPreservedOnRepeatedCleanup()
    {
        using var host = new DeploymentFakeHost { Mode = "persistent" }; await host.StageAsync(foreign: true); var before = DeploymentFakeHost.Read(host.Physical(host.Prepared[0].RuntimeRoot) + "/.hvo-deploy/ownership");
        var result = await host.RunAsync(); await host.RunAsync(); Assert.AreEqual("preserved", result.Array("targets")[0]!.Text("disposition")); Assert.AreEqual(before, DeploymentFakeHost.Read(host.Physical(host.Prepared[0].RuntimeRoot) + "/.hvo-deploy/ownership"));
    }
    [TestMethod]
    public async Task QuotedPaths_AreLiteralAcrossEveryHostPrimitive()
    {
        using var host = new DeploymentFakeHost(); host.SetProductRoot(host.LogicalRoot + "/partial quoted;false'$(touch nope)-skymonitor"); await host.StageAsync(); await host.RunAsync();
        Assert.IsFalse(Directory.Exists(host.Physical(host.Prepared[0].RuntimeRoot))); Assert.IsFalse(File.Exists("nope"));
    }
    [TestMethod]
    [DataRow("logic")][DataRow("east")]
    public async Task Schema7RetainedBytes_UseHistoricalDockerProject(string name)
    {
        using var host = new DeploymentFakeHost(); await host.StageAsync(legacy: true); var target = host.Contract.Targets.Single(t => t.Name == name);
        Assert.AreEqual("hvo-test-" + name, host.Contract.Project(target));
        host.Resource = target.DockerContext + ":container:label=com.docker.compose.project=" + host.Contract.Project(target);
        await Reject(host, "unexpected-deployment-resource"); host.Resource = null; var result = await host.RunAsync(inspect: true); Assert.AreEqual(host.Contract.Sha256, result.Text("inventorySha256"));
    }
    [TestMethod]
    [DataRow("v8-field")][DataRow("collision")][DataRow("historical-path")][DataRow("missing-tmp")]
    public async Task Schema7Validation_PreservesHistoricalShapeAndPath(string mutation)
    {
        using var host = new DeploymentFakeHost(); host.Legacy();
        if (mutation == "v8-field") host.Inventory["logicHost"]!["instanceId"] = "00000000-0000-4000-8000-000000000001";
        if (mutation == "collision") host.Inventory["cameraAgents"]![0]!["sshHost"] = host.Inventory["logicHost"]!["sshHost"]!.DeepClone();
        if (mutation == "historical-path") host.Inventory["deployment"]!["catalog"]!["installRoot"] = host.Inventory["deployment"]!["catalog"]!.Text("installRoot") + ",retained";
        DeploymentFakeHost.WriteJson(host.InventoryPath, host.Inventory);
        if (mutation is "historical-path" or "missing-tmp") _ = await SplitHostInventory.ReadAsync(host.InventoryPath, "isolated", mutation == "missing-tmp" ? new MissingTempRunner() : new ProcessRunner(), CancellationToken.None);
        else await Assert.ThrowsExactlyAsync<InstallerException>(() => SplitHostInventory.ReadAsync(host.InventoryPath, "isolated", new ProcessRunner(), CancellationToken.None));
    }
    [TestMethod]
    [DataRow("ledger")][DataRow("manifest")][DataRow("evidence")][DataRow("commit")]
    public async Task InterruptedPublication_RepairsOnlyTheNextUncommittedGeneration(string point)
    {
        using var host = new DeploymentFakeHost(); await host.StageAsync(); var fired = false;
        await Assert.ThrowsExactlyAsync<PartialPrepareCrashException>(() => host.RunAsync(boundary: name => { if (!fired && name == "abrupt-after-partial-prepare-cleanup-" + point) { fired = true; throw new PartialPrepareCrashException(); } }));
        await host.RunAsync(); Assert.IsTrue(fired);
    }
    [TestMethod]
    [DataRow("manifest")][DataRow("evidence")][DataRow("commit")][DataRow("orphan")][DataRow("generation")][DataRow("resource")]
    public async Task CommittedJournalTampering_IsRejected(string mutation)
    {
        using var host = new DeploymentFakeHost(); await host.StageAsync(); await host.RunAsync(inspect: true);
        var ledgerPath = host.StateRoot + "/partial-prepare-inspect-ledger.json";
        if (mutation == "orphan") File.Delete(ledgerPath);
        else if (mutation == "resource") { var ledger = Parse(DeploymentFakeHost.Read(ledgerPath)); ledger.Array("resources").Add(new JsonObject { ["resource"] = "target:foreign", ["action"] = "cleanup-runtime-artifacts", ["status"] = "completed", ["intentAt"] = Now(), ["updatedAt"] = Now() }); DeploymentFakeHost.WriteJson(ledgerPath, ledger); }
        else
        {
            var path = mutation == "evidence" ? host.EvidenceRoot + "/partial-prepare-inspect.json" : mutation == "manifest" ? host.StateRoot + "/partial-prepare-inspect-manifest.json" : mutation == "generation" ? ledgerPath : host.StateRoot + "/partial-prepare-inspect-commit.json";
            var value = Parse(DeploymentFakeHost.Read(path)); value[mutation == "generation" ? "publicationGeneration" : "unexpected"] = 900; DeploymentFakeHost.WriteJson(path, value);
        }
        await Assert.ThrowsExactlyAsync<InstallerException>(() => host.RunAsync(inspect: true)); Assert.IsTrue(Directory.Exists(host.Physical(host.Prepared[0].RuntimeRoot)));
    }
    [TestMethod]
    [DataRow("up-ledger.json")][DataRow("down-manifest.json")][DataRow("up-rendered")][DataRow("private")][DataRow("prepare-passed")][DataRow("prepare-empty")][DataRow("preflight")]
    public async Task IncompleteOrLaterLifecycle_IsRefused(string boundary)
    {
        using var host = new DeploymentFakeHost(); await host.StageAsync();
        if (boundary == "private") DeploymentFakeHost.Write(host.StateRoot + "/private-upload-registry.json", "[{}]");
        else if (boundary == "prepare-passed" || boundary == "prepare-empty") { var ledger = Parse(DeploymentFakeHost.Read(host.StateRoot + "/prepare-ledger.json")); if (boundary == "prepare-passed") ledger["phaseStatus"] = "passed"; else ledger["targets"] = new JsonArray(); DeploymentFakeHost.WriteJson(host.StateRoot + "/prepare-ledger.json", ledger); }
        else if (boundary == "preflight") File.Delete(host.EvidenceRoot + "/preflight.json");
        else DeploymentFakeHost.Write(host.StateRoot + "/" + boundary, "{}");
        await Assert.ThrowsExactlyAsync<InstallerException>(() => host.RunAsync()); Assert.IsTrue(Directory.Exists(host.Physical(host.Prepared[0].RuntimeRoot)));
    }
    [TestMethod]
    public async Task LocalAndRemoteLocks_ExcludeConcurrentOwners()
    {
        using var host = new DeploymentFakeHost(); await host.StageAsync();
        using (var state = new PartialPrepareState(host.StateRoot, host.EvidenceRoot, false, _ => { })) await Reject(host, "run-already-active");
        var path = host.Physical(Path.GetDirectoryName(host.Prepared[0].RuntimeRoot) + "/" + SplitHostInventory.LockName(host.Contract.Targets[0]));
        using var handle = NativeLinux.OpenReadOnlyNoFollow(path); Assert.IsTrue(NativeLinux.TryLockExclusive(handle, path)); await Assert.ThrowsExactlyAsync<InstallerException>(() => host.RunAsync());
    }
    [TestMethod]
    public async Task CancellationAfterIntent_PreservesResumableState()
    {
        using var host = new DeploymentFakeHost(); await host.StageAsync(); using var cancellation = new CancellationTokenSource();
        var invoked = false; host.BeforePrimitive = (operation, _) => { if (!invoked && operation == "move") { invoked = true; cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); } };
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => host.RunAsync(token: cancellation.Token)); host.BeforePrimitive = null;
        Assert.AreEqual("failed", Parse(DeploymentFakeHost.Read(host.StateRoot + "/partial-prepare-cleanup-ledger.json")).Text("phaseStatus")); await host.RunAsync();
    }
    [TestMethod]
    [DataRow("symlink")][DataRow("hardlink")][DataRow("mode")][DataRow("foreign-entry")]
    public async Task UnsafeRemoteEntries_RejectDeletion(string kind)
    {
        using var host = new DeploymentFakeHost(); await host.StageAsync(); var path = host.Physical(host.Prepared[0].RuntimeRoot) + "/.hvo-deploy/ownership";
        if (kind == "symlink") { File.Move(path, path + ".saved"); File.CreateSymbolicLink(path, path + ".saved"); }
        else if (kind == "hardlink") { var r = await new ProcessRunner().RunAsync("ln", [path, path + ".alias"], CancellationToken.None); Assert.AreEqual(0, r.ExitCode); }
        else if (kind == "mode") File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        else Directory.CreateDirectory(Path.GetDirectoryName(path) + "/foreign");
        await Reject(host, "provenance-or-content-invalid"); Assert.IsTrue(File.Exists(path));
    }
    [TestMethod]
    public async Task MissingOrContradictoryConfirmationAndWrongRun_AreRejected()
    {
        using var host = new DeploymentFakeHost(); await host.StageAsync();
        var help = ((HelpCommand)CommandLine.ParseCommand(["partial-prepare-cleanup", "--help"])).Usage;
        StringAssert.Contains(help, "Inspect recovery candidates", StringComparison.Ordinal); Assert.DoesNotContain("--plan", help, StringComparison.Ordinal);
        Assert.ThrowsExactly<InstallUsageException>(() => (host.Request() with { Confirmation = null }).Validate());
        Assert.ThrowsExactly<InstallUsageException>(() => (host.Request(true) with { Confirmation = host.RunId }).Validate());
        await Assert.ThrowsExactlyAsync<InstallerException>(() => new PartialPrepareRecovery(host, new ProcessRunner()).RunAsync(host.Request() with { Confirmation = "wrong-run" }, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InstallerException>(() => new PartialPrepareRecovery(host, new ProcessRunner()).RunAsync(host.Request(true) with { RunId = "wrong-run" }, CancellationToken.None));
    }
    [TestMethod]
    public async Task SshPrimitiveAdapter_PerformsTheSameDescriptorBoundCleanup()
    {
        using var host = new DeploymentFakeHost { UseShellPrimitives = true }; await host.StageAsync();
        await host.RunAsync(inspect: true); await host.RunAsync(); await host.RunAsync();
        Assert.IsFalse(Directory.Exists(host.Physical(host.Prepared[0].RuntimeRoot)));
    }
    [TestMethod]
    public async Task ExistingServiceInfrastructureContext_IsStillScannedForRunResources()
    {
        using var host = new DeploymentFakeHost();
        host.Inventory["sharedServices"] = Parse("{\"name\":\"shared\",\"sshHost\":\"shared@example\",\"dockerContext\":\"shared-context\",\"expectedArchitecture\":\"amd64\",\"expectedHostName\":\"shared-node\",\"expectedHostIdentity\":\"shared-machine\",\"expectedDockerDaemonIdentity\":\"shared-daemon\",\"runtimeRoot\":\"" + host.LogicalRoot + "/shared-services\",\"runtimeOwner\":\"operator\",\"ports\":[4444]}");
        host.Inventory["sharedServices"]!["runtimeOwner"] = NativeLinux.UserName(NativeLinux.getuid());
        await host.StageAsync(); host.Resource = "shared-context:container:label=io.hvoskymonitor.run-id=" + host.RunId;
        await Reject(host, "unexpected-deployment-resource");
    }
    [TestMethod]
    public async Task UnsafeLocalEvidenceAndRegistry_AreRejectedWithoutFollowingLinks()
    {
        using var host = new DeploymentFakeHost(); await host.StageAsync();
        var sentinel = host.Directory + "/sentinel"; DeploymentFakeHost.Write(sentinel, "preserve\n");
        var registry = host.StateRoot + "/private-upload-registry.json"; File.Delete(registry); File.CreateSymbolicLink(registry, sentinel);
        await Assert.ThrowsExactlyAsync<InstallerException>(() => host.RunAsync()); Assert.AreEqual("preserve\n", DeploymentFakeHost.Read(sentinel));
    }
    [TestMethod]
    public async Task UnmigratedShellPhases_RejectSchema7AndFailedPrepareBeforeAnyHostCall()
    {
        using var host = new DeploymentFakeHost();
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(repository.FullName + "/scripts/deploy:environment")) repository = repository.Parent;
        Assert.IsNotNull(repository);
        var runner = new ProcessRunner(); var revision = await runner.RunAsync("git", ["-C", repository.FullName, "rev-parse", "HEAD"], CancellationToken.None);
        host.Inventory["source"]!["revision"] = revision.StandardOutput.Trim(); host.Inventory["images"]!["tag"] = "rev-" + revision.StandardOutput.Trim();
        await host.StageAsync();
        var gitState = await runner.RunAsync("git", ["-C", repository.FullName, "status", "--porcelain"], CancellationToken.None);
        foreach (var path in new[] { host.StateRoot + "/manifest.json", host.EvidenceRoot + "/preflight.json" })
        {
            var value = Parse(DeploymentFakeHost.Read(path)); value["source"]!["worktreeState"] = gitState.StandardOutput.Length == 0 ? "clean" : "dirty"; DeploymentFakeHost.WriteJson(path, value);
        }
        var down = await runner.RunAsync(repository.FullName + "/scripts/deploy:environment", ["down", "--inventory", host.InventoryPath, "--mode", "isolated", "--run-id", host.RunId, "--state-root", host.StateRoot, "--evidence-root", host.EvidenceRoot, "--delete-state", "--confirm", host.RunId], CancellationToken.None);
        Assert.AreNotEqual(0, down.ExitCode); StringAssert.Contains(down.StandardError, "mismatch-or-not-passed", StringComparison.Ordinal);
        host.Legacy(); DeploymentFakeHost.WriteJson(host.InventoryPath, host.Inventory);
        var preflight = await runner.RunAsync(repository.FullName + "/scripts/deploy:environment", ["preflight", "--inventory", host.InventoryPath, "--mode", "isolated", "--run-id", host.RunId], CancellationToken.None);
        Assert.AreNotEqual(0, preflight.ExitCode); StringAssert.Contains(preflight.StandardError, "schema-or-value-invalid", StringComparison.Ordinal);
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DockerOutageOrMalformedAbsence_NeverAuthorizesDeletion(bool whitespace)
    {
        using var host = new DeploymentFakeHost(); await host.StageAsync();
        if (whitespace) { host.Resource = "logic-context:container:label=io.hvoskymonitor.run-id=" + host.RunId; host.ResourceOutput = " "; }
        else host.DockerResponse = args => args.Count > 3 && args[3] == "inspect" ? new ProcessResult(1, "", "authentication failed") : null;
        await Reject(host, "unexpected-deployment-resource");
    }
    [TestMethod]
    [DataRow(".ownership.cleanup.")]
    [DataRow(".hvo-deploy.cleanup.")]
    [DataRow(".hvo-deploy-cleanup-root-")]
    [DataRow(".state.cleanup.")]
    [DataRow(".lock.cleanup.")]
    public async Task InterruptionAfterQuarantineRename_ResumesWithoutTraversingTheTree(string nameFragment)
    {
        using var host = new DeploymentFakeHost(); await host.StageAsync(); var fired = false;
        host.BeforePrimitive = (operation, args) =>
        {
            if (!fired && operation is "unlink" or "rmdir" && Path.GetFileName(args[0]).Contains(nameFragment, StringComparison.Ordinal)) { fired = true; throw new PartialPrepareCrashException(); }
        };
        await Assert.ThrowsExactlyAsync<PartialPrepareCrashException>(() => host.RunAsync()); Assert.IsTrue(fired); host.BeforePrimitive = null;
        await host.RunAsync(); Assert.IsFalse(Directory.Exists(host.Physical(host.Prepared[0].RuntimeRoot)));
    }
    private sealed class MissingTempRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken token) =>
            new ProcessRunner().RunAsync("env", ["TMPDIR=/untrusted-missing-tmp", fileName, .. arguments], token);
    }
    private static async Task Reject(DeploymentFakeHost host, string code, bool inspect = true)
    {
        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => host.RunAsync(inspect)); StringAssert.Contains(exception.Message, code, StringComparison.Ordinal); Assert.IsTrue(Directory.Exists(host.Physical(host.Prepared[0].RuntimeRoot)));
    }
}
