using System.Text.Json.Nodes;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
[TestCategory("Unit")]
public sealed class ExistingDownTests
{
    [TestMethod]
    [DataRow("Off", "")]
    [DataRow("Off", "changing-then-stable")]
    [DataRow("Off", "stale-async")]
    [DataRow("Off", "delay-west")]
    [DataRow("Off", "pascal")]
    [DataRow("Hybrid", "")]
    public async Task GracefulStop_PreservesExistingResourcesAndDiagnostics_AndRepeats(string mode, string fault)
    {
        using var fixture = new ExistingDownFixture { Mode = mode, Fault = fault }; await fixture.StageAsync();
        var result = await fixture.RunAsync(); Assert.AreEqual("passed", result.Text("phaseStatus")); Assert.AreEqual("preserve", result.Text("policy"));
        Assert.AreEqual(4, result.Array("resources").Count); Assert.IsTrue(result.Array("resources").All(n => n!.Text("status") == "completed"));
        Assert.AreEqual(3, fixture.Stops); Assert.AreEqual(4, fixture.ControlKeys.Count); if (fault == "changing-then-stable") Assert.AreEqual(4, fixture.EastReads, "A,B,B then final observation");
        Assert.AreEqual(fixture.Queries.Where(q => q.Contains('?', StringComparison.Ordinal)).Count(), fixture.Queries.Where(q => q.Contains('?', StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Count(), "Each observation and continuity query must be fresh.");
        AssertPrivateAbsent(fixture); AssertPreserved(fixture);
        var privateLog = fixture.Host.StateRoot + "/down-private/east-summary-1.json"; var before = DeploymentFakeHost.Read(privateLog);
        await fixture.RunAsync(); Assert.AreEqual(3, fixture.Stops); Assert.AreEqual(before, DeploymentFakeHost.Read(privateLog)); AssertPrivateAbsent(fixture); AssertPreserved(fixture);
    }
    [TestMethod]
    [DataRow("pause-conflict", "unexpected-pause-status")]
    [DataRow("pause-receipt", "invalid-pause-receipt")]
    [DataRow("mismatch", "invalid-continuity-boundary")]
    [DataRow("duplicate", "invalid-continuity-boundary")]
    [DataRow("change", "continuity-boundary-changed")]
    [DataRow("hash-drift", "continuity-boundary-changed")]
    [DataRow("mode", "drain-timeout")]
    [DataRow("pre-pause", "drain-timeout")]
    [DataRow("wrong", "drain-timeout")]
    [DataRow("stale", "drain-timeout")]
    [DataRow("third", "drain-timeout")]
    [DataRow("excess", "drain-timeout")]
    [DataRow("fractional", "drain-timeout")]
    [DataRow("excess-raw", "drain-timeout")]
    [DataRow("fractional-raw", "drain-timeout")]
    [DataRow("retry", "drain-timeout")]
    [DataRow("quarantine", "drain-timeout")]
    [DataRow("pressure", "drain-timeout")]
    [DataRow("unrelated", "drain-timeout")]
    [DataRow("busy", "drain-timeout")]
    [DataRow("unhealthy", "drain-timeout")]
    [DataRow("stale-section", "drain-timeout")]
    [DataRow("stale-hybrid-worker", "drain-timeout")]
    [DataRow("config-mismatch", "drain-timeout")]
    [DataRow("config-not-current", "drain-timeout")]
    [DataRow("config-mode-missing", "drain-timeout")]
    [DataRow("malformed", "drain-timeout")]
    [DataRow("terminal", "drain-timeout")]
    [DataRow("equal-captured", "drain-timeout")]
    [DataRow("decreasing-captured", "drain-timeout")]
    [DataRow("pre-pause-captured", "drain-timeout")]
    [DataRow("changing", "drain-timeout")]
    [DataRow("final-version", "final-pause-version-mismatch")]
    [DataRow("final-unsafe", "final-observation-unsafe")]
    [DataRow("final-fingerprint", "final-observation-changed")]
    [DataRow("final-time", "final-observation-changed")]
    [DataRow("final-hash", "final-continuity-boundary-changed")]
    [DataRow("safe-unsafe", "drain-timeout")]
    [DataRow("leased", "drain-timeout")]
    public async Task UnsafeBoundary_RefusesBeforeAnyComposeStop_AndCleansPrivateArtifacts(string fault, string code)
    {
        var off = new[] { "mode", "final-version", "final-unsafe", "final-fingerprint", "final-time", "final-hash", "safe-unsafe", "leased", "pause-conflict", "pause-receipt", "mismatch", "duplicate", "change", "hash-drift" };
        using var fixture = new ExistingDownFixture { Fault = fault, Mode = off.Contains(fault, StringComparer.Ordinal) ? "Off" : "Hybrid" }; await fixture.StageAsync();
        var exception = await Assert.ThrowsAsync<InstallerException>(() => fixture.RunAsync()); StringAssert.Contains(exception.Message, code, StringComparison.Ordinal);
        Assert.AreEqual(0, fixture.Stops); AssertPrivateAbsent(fixture); AssertPreserved(fixture);
        var ledger = Parse(DeploymentFakeHost.Read(fixture.Host.StateRoot + "/down-ledger.json")); Assert.AreEqual("failed", ledger.Text("phaseStatus")); Assert.IsFalse(ledger.ContainsKey("completedAt"));
        Assert.IsFalse(exception.Message.Contains("fixture-secret", StringComparison.Ordinal));
    }
    [TestMethod]
    [DataRow("abrupt-after-down-ledger")]
    [DataRow("abrupt-after-down-manifest")]
    [DataRow("abrupt-after-down-evidence")]
    [DataRow("abrupt-after-down-commit")]
    [DataRow("abrupt-after-down-cleanup")]
    [DataRow("after-graceful-stop-mutation-cameraagent:east")]
    [DataRow("after-graceful-stop-cameraagent:east")]
    public async Task InterruptedTeardown_RecoversEveryDurableBoundary(string failpoint)
    {
        using var fixture = new ExistingDownFixture(); await fixture.StageAsync();
        var fired = false;
        await Assert.ThrowsAsync<PartialPrepareCrashException>(() => fixture.RunAsync(point => { if (!fired && point == failpoint) { fired = true; throw new PartialPrepareCrashException(); } })); Assert.IsTrue(fired);
        await fixture.RunAsync(); AssertPrivateAbsent(fixture); AssertPreserved(fixture);
        var state = fixture.Host.StateRoot; var ledger = Parse(DeploymentFakeHost.Read(state + "/down-ledger.json"));
        Assert.AreEqual("passed", ledger.Text("phaseStatus")); Assert.AreEqual(Canonical(ledger), Canonical(Parse(DeploymentFakeHost.Read(state + "/down-manifest.json")))); Assert.AreEqual(Canonical(ledger), Canonical(Parse(DeploymentFakeHost.Read(fixture.Host.EvidenceRoot + "/down.json"))));
        Assert.AreEqual(Hash(Canonical(ledger) + "\n"), Parse(DeploymentFakeHost.Read(state + "/down-commit.json")).Text("ledgerSha256"));
    }
    [TestMethod]
    [DataRow("orphan")]
    [DataRow("tamper")]
    [DataRow("committed-mirror")]
    [DataRow("registry-link")]
    [DataRow("failed-prepare")]
    [DataRow("lock")]
    [DataRow("delete")]
    public async Task InvalidAuthority_RefusesWithoutMutation(string fault)
    {
        using var fixture = new ExistingDownFixture(); await fixture.StageAsync(); var state = fixture.Host.StateRoot;
        if (fault is "orphan" or "tamper" or "committed-mirror") await fixture.RunAsync(); var stops = fixture.Stops;
        var manifest = File.Exists(state + "/down-manifest.json") ? DeploymentFakeHost.Read(state + "/down-manifest.json") : "";
        PartialPrepareState? held = null;
        try
        {
            if (fault == "orphan") File.Delete(state + "/down-ledger.json");
            if (fault == "tamper") { var ledger = Parse(DeploymentFakeHost.Read(state + "/down-ledger.json")); ledger["publicationGeneration"] = ledger["publicationGeneration"]!.GetValue<int>() + 1; ledger["unexpectedCrashField"] = true; DeploymentFakeHost.WriteJson(state + "/down-ledger.json", ledger); }
            if (fault == "committed-mirror") DeploymentFakeHost.Write(state + "/down-manifest.json", "{}");
            if (fault == "registry-link") { File.Move(state + "/private-upload-registry.json", state + "/original-registry"); File.CreateSymbolicLink(state + "/private-upload-registry.json", state + "/original-registry"); }
            if (fault == "failed-prepare") { var prepare = Parse(DeploymentFakeHost.Read(state + "/prepare-ledger.json")); prepare["phaseStatus"] = "failed"; DeploymentFakeHost.WriteJson(state + "/prepare-ledger.json", prepare); }
            if (fault == "lock") held = new PartialPrepareState(state, fixture.Host.EvidenceRoot, false, _ => { }, "down");
            var request = fault == "delete" ? fixture.Request with { Policy = "delete", Confirmation = fixture.Host.RunId } : fixture.Request;
            await Assert.ThrowsAsync<InstallerException>(() => new ExistingDown(fixture.Host, new ProcessRunner(), "/source").RunAsync(request, CancellationToken.None)); Assert.AreEqual(stops, fixture.Stops);
            if (fault == "delete") Assert.IsFalse(Directory.Exists(state + "/down-rendered"));
            if (fault is "orphan" or "tamper") Assert.AreEqual(manifest, DeploymentFakeHost.Read(state + "/down-manifest.json"));
            AssertPreserved(fixture);
        }
        finally { held?.Dispose(); }
    }
    [TestMethod]
    public async Task CancellationAfterIntent_RetainsFailureAndResumablePrivateCleanup()
    {
        using var fixture = new ExistingDownFixture(); await fixture.StageAsync(); using var canceled = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.RunAsync(point => { if (point == "abrupt-after-down-commit") canceled.Cancel(); }, canceled.Token));
        AssertPrivateAbsent(fixture); Assert.AreEqual(0, fixture.Stops); await fixture.RunAsync(); Assert.AreEqual(3, fixture.Stops);
    }
    [TestMethod]
    [DataRow("host")]
    [DataRow("daemon")]
    [DataRow("compose")]
    [DataRow("recreated")]
    public async Task HostAndDockerFailures_NeverBecomeVerifiedAbsence(string fault)
    {
        using var fixture = new ExistingDownFixture(); await fixture.StageAsync();
        if (fault is "host" or "daemon") { await fixture.RunAsync(); if (fault == "host") fixture.Host.BadHost = "east"; else fixture.Host.BadDaemon = "east"; }
        else fixture.Fault = fault;
        var stops = fixture.Stops; await Assert.ThrowsAsync<InstallerException>(() => fixture.RunAsync());
        AssertPreserved(fixture); if (fault is "host" or "daemon") Assert.AreEqual(stops, fixture.Stops);
    }
    [TestMethod]
    [DataRow("down", "owner.cookies")]
    [DataRow("bootstrap", "bootstrap-request.json")]
    [DataRow("smoke", "owner.headers")]
    public async Task RegisteredUploadAndCompletedCredentials_AreReconciledOnRepeat(string phase, string filename)
    {
        using var fixture = new ExistingDownFixture(); await fixture.StageAsync();
        var target = fixture.Host.Contract.Targets.Single(t => t.Name == "east"); var upload = target.RuntimeRoot + "/.hvo-deploy/uploads/hvo-upload-down-123-" + new string('a', 32) + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Host.Physical(upload))!, SafeFileSystem.OwnerDirectoryMode); DeploymentFakeHost.Write(fixture.Host.Physical(upload), "interrupted");
        void Register(string path, string kind, string ownerPhase = "down") => DeploymentFakeHost.WriteJson(fixture.Host.StateRoot + "/private-upload-registry.json", new JsonArray(new JsonObject { ["phase"] = ownerPhase, ["kind"] = kind, ["path"] = path, ["target"] = fixture.Host.Inventory.Array("cameraAgents").Single(n => n!.Text("name") == "east")!.DeepClone() }));
        Register(upload, "upload-temp"); fixture.Host.BadHost = "east"; await Assert.ThrowsAsync<InstallerException>(() => fixture.RunAsync()); Assert.IsTrue(File.Exists(fixture.Host.Physical(upload))); Assert.AreEqual(1, JsonNode.Parse(DeploymentFakeHost.Read(fixture.Host.StateRoot + "/private-upload-registry.json"))!.AsArray().Count);
        fixture.Host.BadHost = null; await fixture.RunAsync(); Assert.IsFalse(File.Exists(fixture.Host.Physical(upload)));
        var credential = target.RuntimeRoot + "/.hvo-deploy/" + phase + "-" + fixture.Host.RunId + "/" + filename; Directory.CreateDirectory(Path.GetDirectoryName(fixture.Host.Physical(credential))!, SafeFileSystem.OwnerDirectoryMode); DeploymentFakeHost.Write(fixture.Host.Physical(credential), "credential"); Register(credential, "remote-private", phase);
        await fixture.RunAsync(); AssertPrivateAbsent(fixture); Assert.IsFalse(File.Exists(fixture.Host.Physical(credential))); Assert.AreEqual(3, fixture.Stops);
    }
    [TestMethod]
    public void Continuity_AcceptsZeroAndRejectsMalformedSequenceContainer()
    {
        var value = ExistingDownFixture.Continuity(); value["durable"]!["captureSequences"]![0]!["lastSequence"] = 0; Assert.AreEqual(0, DownObservation.Continuity(value).Sequence);
        value["durable"]!["captureSequences"] = new JsonObject { ["malformed"] = true }; Assert.Throws<InstallerException>(() => DownObservation.Continuity(value));
    }
    [TestMethod]
    public async Task RealSshPrimitives_RunTheTypedPhaseWithLiteralPaths()
    {
        using var fixture = new ExistingDownFixture(); fixture.Host.SetProductRoot(fixture.Host.LogicalRoot + "/remote/quoted space '$;literal/skymonitor");
        fixture.Host.UseShellPrimitives = true; await fixture.StageAsync(); await fixture.RunAsync(); AssertPrivateAbsent(fixture); AssertPreserved(fixture);
    }
    [TestMethod]
    public async Task DockerContextShellBoundary_ScrubsEveryAmbientOverrideAndPreservesArguments()
    {
        using var fixture = new ExistingDownFixture(); var path = fixture.Host.Directory + "/docker";
        DeploymentFakeHost.Write(path, "#!/usr/bin/env bash\nset -eu\n[[ -z ${DOCKER_HOST+x} && -z ${DOCKER_CONTEXT+x} && -z ${DOCKER_TLS_VERIFY+x} && -z ${DOCKER_CERT_PATH+x} ]]\nprintf '%s\\n' \"$@\"\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var result = await new ProcessRunner().RunAsync("env", ["PATH=" + fixture.Host.Directory + ":" + Environment.GetEnvironmentVariable("PATH"), "DOCKER_HOST=invalid", "DOCKER_CONTEXT=invalid", "DOCKER_TLS_VERIFY=1", "DOCKER_CERT_PATH=/unrelated", "bash", "-c", SplitHostInventory.Resource("TeardownContext.sh"), "boundary", "--host", "ssh://fixture", "compose", "--env-file", "literal ' $;file"], CancellationToken.None);
        Assert.AreEqual(0, result.ExitCode); Assert.AreEqual("--host\nssh://fixture\ncompose\n--env-file\nliteral ' $;file\n", result.StandardOutput);
    }
    [TestMethod]
    public async Task SameHostMultipleInstances_KeepIndependentProjectsAndDurableState()
    {
        using var fixture = new ExistingDownFixture(); var logic = fixture.Host.Inventory["logicHost"]!;
        foreach (var camera in fixture.Host.Inventory.Array("cameraAgents"))
            foreach (var key in new[] { "sshHost", "dockerContext", "expectedHostName", "expectedHostIdentity", "expectedDockerDaemonIdentity", "expectedArchitecture" }) camera![key] = logic[key]!.DeepClone();
        await fixture.StageAsync(); await fixture.RunAsync(); Assert.AreEqual(3, fixture.Stops); AssertPreserved(fixture); AssertPrivateAbsent(fixture);
        var projects = fixture.Host.DockerCalls.Where(args => args.Contains("stop", StringComparer.Ordinal)).Select(args => args[4]).ToArray(); Assert.AreEqual(3, projects.Distinct(StringComparer.Ordinal).Count());
    }
    [TestMethod]
    public void RealSshPrimitiveWrite_PreservesExactMultilineBytesAndTrailingNewlines()
    {
        using var fixture = new ExistingDownFixture(); using var files = new SshDeploymentFiles("", CancellationToken.None, local: true);
        var path = fixture.Host.Directory + "/literal-private-file"; const string content = "RequestVerificationToken: value\nIdempotency-Key: unique-key\n\n";
        files.Call("write", path, content); Assert.AreEqual(content, DeploymentFakeHost.Read(path));
    }
    [TestMethod]
    public async Task InvalidRegistryEntry_PreventsCleanupOfEarlierValidEntries()
    {
        using var fixture = new ExistingDownFixture(); await fixture.StageAsync(); var target = fixture.Host.Contract.Targets.Single(t => t.Name == "east");
        var root = target.RuntimeRoot + "/.hvo-deploy/down-" + fixture.Host.RunId; Directory.CreateDirectory(fixture.Host.Physical(root), SafeFileSystem.OwnerDirectoryMode);
        var path = root + "/owner.cookies"; DeploymentFakeHost.Write(fixture.Host.Physical(path), "preserve-before-validation");
        var node = fixture.Host.Inventory.Array("cameraAgents").Single(n => n!.Text("name") == "east")!;
        JsonObject Entry(string file) => new() { ["phase"] = "down", ["kind"] = "remote-private", ["target"] = node.DeepClone(), ["path"] = file };
        DeploymentFakeHost.WriteJson(fixture.Host.StateRoot + "/private-upload-registry.json", new JsonArray(Entry(path), Entry(fixture.Host.Directory + "/unrelated")));
        await Assert.ThrowsAsync<InstallerException>(() => fixture.RunAsync()); Assert.AreEqual("preserve-before-validation", DeploymentFakeHost.Read(fixture.Host.Physical(path))); Assert.AreEqual(0, fixture.Stops);
    }
    [TestMethod]
    public async Task PersistentExistingServices_KeepAllRuntimeStateAndRepeatSafely()
    {
        using var fixture = new ExistingDownFixture(); fixture.Host.Mode = "persistent"; await fixture.StageAsync();
        await fixture.RunAsync(); await fixture.RunAsync(); Assert.AreEqual(3, fixture.Stops); AssertPrivateAbsent(fixture); AssertPreserved(fixture);
    }
    [TestMethod]
    public async Task RegistryOver256Entries_RefusesBeforeAnyRemoteWork()
    {
        using var fixture = new ExistingDownFixture(); await fixture.StageAsync(); var node = fixture.Host.Inventory.Array("cameraAgents")[0]!;
        var entries = new JsonArray(Enumerable.Range(0, 257).Select(i => (JsonNode)new JsonObject { ["phase"] = "down", ["kind"] = "remote-private", ["target"] = node.DeepClone(), ["path"] = node.Text("runtimeRoot") + "/.hvo-deploy/down-" + fixture.Host.RunId + "/p-" + i + "-control.headers" }).ToArray());
        DeploymentFakeHost.WriteJson(fixture.Host.StateRoot + "/private-upload-registry.json", entries);
        await Assert.ThrowsAsync<InstallerException>(() => fixture.RunAsync()); Assert.AreEqual(0, fixture.Host.DockerCalls.Count); Assert.AreEqual(0, fixture.Stops);
    }
    [TestMethod]
    public async Task ReadOnlyOwnerSecret_IsAcceptedWithoutChangingItsModeOrBytes()
    {
        using var fixture = new ExistingDownFixture(); await fixture.StageAsync(); var path = fixture.Host.Directory + "/secrets.env";
        var bytes = DeploymentFakeHost.Read(path); File.SetUnixFileMode(path, UnixFileMode.UserRead);
        await fixture.RunAsync(); Assert.AreEqual(bytes, DeploymentFakeHost.Read(path)); Assert.AreEqual(UnixFileMode.UserRead, File.GetUnixFileMode(path)); AssertPrivateAbsent(fixture);
    }
    [TestMethod]
    public void PauseReceipt_AcceptsEquivalentNumericEnumRepresentations()
    {
        var receipt = Parse("{\"state\":3.0,\"version\":0,\"changed\":false,\"replayed\":false,\"requestedUtc\":\"2026-08-07T00:00:00Z\",\"completedUtc\":\"2026-08-07T00:00:01Z\"}");
        Assert.AreEqual(0, DownObservation.Pause(receipt).Version);
    }
    [TestMethod]
    public async Task UnreachableHost_DoesNotPreventPrivateCleanupOnOtherTargets()
    {
        using var fixture = new ExistingDownFixture(); await fixture.StageAsync(); var entries = new JsonArray(); var paths = new List<string>();
        foreach (var node in fixture.Host.Inventory.Array("cameraAgents"))
        {
            var path = node!.Text("runtimeRoot") + "/.hvo-deploy/down-" + fixture.Host.RunId + "/owner.cookies";
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Host.Physical(path))!, SafeFileSystem.OwnerDirectoryMode); DeploymentFakeHost.Write(fixture.Host.Physical(path), "credential"); paths.Add(path);
            entries.Add(new JsonObject { ["phase"] = "down", ["kind"] = "remote-private", ["target"] = node!.DeepClone(), ["path"] = path });
        }
        DeploymentFakeHost.WriteJson(fixture.Host.StateRoot + "/private-upload-registry.json", entries); fixture.Host.BadHost = "east";
        await Assert.ThrowsAsync<InstallerException>(() => fixture.RunAsync()); Assert.IsTrue(File.Exists(fixture.Host.Physical(paths[0]))); Assert.IsFalse(File.Exists(fixture.Host.Physical(paths[1])));
        var retained = JsonNode.Parse(DeploymentFakeHost.Read(fixture.Host.StateRoot + "/private-upload-registry.json"))!.AsArray(); Assert.AreEqual(1, retained.Count); Assert.AreEqual(paths[0], retained[0]!.Text("path")); Assert.AreEqual(0, fixture.Stops);
    }
    [TestMethod]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("malformed")]
    [DataRow("control")]
    [DataRow("symlink")]
    public async Task InvalidSecretSource_RefusesBeforeStatePublicationOrHostCalls(string fault)
    {
        using var fixture = new ExistingDownFixture(); await fixture.StageAsync(); var path = fixture.Host.Directory + "/secrets.env";
        var ownerRefs = fixture.Host.Inventory.Array("cameraAgents").Select(n => n!.Text("ownerPasswordSecretReference")).ToHashSet(StringComparer.Ordinal);
        var unrelated = fixture.Host.Inventory["secretSource"]!.Array("requiredReferences").Select(n => n!.GetValue<string>()).First(reference => !ownerRefs.Contains(reference));
        if (fault == "missing") DeploymentFakeHost.Write(path, string.Join('\n', DeploymentFakeHost.Read(path).Split('\n').Where(line => !line.StartsWith(unrelated + "=", StringComparison.Ordinal))));
        if (fault == "duplicate") DeploymentFakeHost.Append(path, "EXTRA=one\nEXTRA=two\n");
        if (fault == "malformed") DeploymentFakeHost.Append(path, "not a secret entry\n");
        if (fault == "control") DeploymentFakeHost.Append(path, "EXTRA=unsafe\tvalue\n");
        if (fault == "symlink") { File.Move(path, path + ".original"); File.CreateSymbolicLink(path, path + ".original"); }
        await Assert.ThrowsAsync<InstallerException>(() => fixture.RunAsync()); Assert.AreEqual(0, fixture.Host.DockerCalls.Count); Assert.IsFalse(File.Exists(fixture.Host.StateRoot + "/down-ledger.json"));
    }
    [TestMethod]
    public async Task PassedPublication_RequiresPrivateRegistryToRemainEmptyAtItsOwnBoundary()
    {
        using var fixture = new ExistingDownFixture(); await fixture.StageAsync();
        await Assert.ThrowsAsync<InstallerException>(() => fixture.RunAsync(point =>
        {
            if (point != "abrupt-after-down-cleanup") return;
            var node = fixture.Host.Inventory.Array("cameraAgents")[0]!; var path = node.Text("runtimeRoot") + "/.hvo-deploy/down-" + fixture.Host.RunId + "/owner.cookies";
            DeploymentFakeHost.Write(fixture.Host.Physical(path), "credential");
            DeploymentFakeHost.WriteJson(fixture.Host.StateRoot + "/private-upload-registry.json", new JsonArray(new JsonObject { ["phase"] = "down", ["kind"] = "remote-private", ["target"] = node.DeepClone(), ["path"] = path }));
        }));
        Assert.AreEqual("failed", Parse(DeploymentFakeHost.Read(fixture.Host.StateRoot + "/down-ledger.json")).Text("phaseStatus")); AssertPrivateAbsent(fixture);
        await fixture.RunAsync(); Assert.AreEqual(3, fixture.Stops);
    }
    [TestMethod]
    [DataRow("write", "owner-password")]
    [DataRow("write", "owner.headers")]
    [DataRow("write", "pause.json")]
    [DataRow("write", "control.headers")]
    [DataRow("http", "login.html")]
    public async Task InterruptedPrivateCreation_RemainsRegisteredUntilVerifiedResumeCleanup(string operation, string suffix)
    {
        using var fixture = new ExistingDownFixture(); await fixture.StageAsync(); string? created = null;
        fixture.Host.AfterPrimitive = (name, args) =>
        {
            if (created is not null || name != operation) return;
            var path = name == "write" ? args[0] : args[Array.IndexOf(args, "--output") + 1];
            if (!path.EndsWith(suffix, StringComparison.Ordinal)) return; created = path; throw new PartialPrepareCrashException();
        };
        await Assert.ThrowsAsync<PartialPrepareCrashException>(() => fixture.RunAsync()); Assert.IsNotNull(created); Assert.IsTrue(File.Exists(fixture.Host.Physical(created)));
        var registry = JsonNode.Parse(DeploymentFakeHost.Read(fixture.Host.StateRoot + "/private-upload-registry.json"))!.AsArray(); Assert.IsTrue(registry.Any(entry => entry!.Text("path") == created));
        fixture.Host.AfterPrimitive = null; await fixture.RunAsync(); AssertPrivateAbsent(fixture); AssertPreserved(fixture); Assert.AreEqual(3, fixture.Stops);
    }
    [TestMethod]
    public async Task OwnerAuthenticationFailure_CleansSeededAndTemporaryCredentials()
    {
        using var fixture = new ExistingDownFixture { Fault = "authentication" }; await fixture.StageAsync();
        var east = fixture.Host.Contract.Targets.Single(t => t.Name == "east");
        var seeded = new[] { east.RuntimeRoot + "/config/private/owner-password", east.RuntimeRoot + "/config/secrets/LocalIdentity__AdminPasswordFile" };
        foreach (var path in seeded) DeploymentFakeHost.Write(fixture.Host.Physical(path), "seeded-credential");
        var error = await Assert.ThrowsAsync<InstallerException>(() => fixture.RunAsync()); StringAssert.Contains(error.Message, "owner-authentication-failed", StringComparison.Ordinal);
        Assert.AreEqual(0, fixture.Stops); AssertPrivateAbsent(fixture); foreach (var path in seeded) Assert.IsFalse(File.Exists(fixture.Host.Physical(path)));
    }
    private static void AssertPreserved(ExistingDownFixture fixture)
    {
        foreach (var target in fixture.Host.Contract.Targets) Assert.AreEqual("keep-me", DeploymentFakeHost.Read(fixture.Host.Physical(target.RuntimeRoot) + "/unrelated-data"));
        Assert.IsFalse(fixture.Host.DockerCalls.Any(args => args.Contains("rm", StringComparer.Ordinal) || args.Contains("down", StringComparer.Ordinal)));
        Assert.IsTrue(fixture.Host.DockerCalls.All(args => args[0] is "context" or "--host"));
    }
    private static void AssertPrivateAbsent(ExistingDownFixture fixture)
    {
        Assert.AreEqual("[]", Canonical(JsonNode.Parse(DeploymentFakeHost.Read(fixture.Host.StateRoot + "/private-upload-registry.json"))));
        foreach (var target in fixture.Host.Contract.Targets.Where(t => t.Component == "cameraAgent"))
        {
            var root = fixture.Host.Physical(target.RuntimeRoot) + "/.hvo-deploy/down-" + fixture.Host.RunId;
            if (Directory.Exists(root)) Assert.AreEqual(0, Directory.EnumerateFiles(root).Count());
        }
    }
}
