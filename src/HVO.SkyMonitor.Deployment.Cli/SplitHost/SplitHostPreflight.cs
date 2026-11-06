using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment;

internal sealed class SplitHostPreflight(IDeploymentHost host, IProcessInputRunner runner, Action<string>? boundary = null,
    Action<string>? status = null)
{
    private static readonly string[] EvidenceFields = ["schemaVersion", "runId", "mode", "inventorySha256", "source", "targets", "checks", "startedAt", "updatedAt", "completedAt", "phaseStatus"];
    private static readonly string[] CorrelationFields = ["name", "hostIdentity", "hostName", "dockerDaemonIdentity", "architecture", "runtimeRoot"];
    private static readonly string[] ResumeFields = ["schemaVersion", "runId", "mode", "inventorySha256", "source", "phaseStatus", "targets", "checks"];
    internal static InstallerException Refuse(string code) => new("preflight: " + code);
    public static async Task<JsonObject> ExecuteAsync(PreflightRequest request, CancellationToken token)
    {
        try
        {
            var repository = new DirectoryInfo(Environment.CurrentDirectory);
            while (repository is not null && !File.Exists(repository.FullName + "/HVO.SkyMonitor.v9.slnx")) repository = repository.Parent;
            if (repository is null) throw Refuse("source-checkout-required");
            var runner = new ProcessRunner(4 * 1024 * 1024);
            var phase = new SplitHostPreflight(new DeploymentHost(), runner, name =>
            {
                if (Environment.GetEnvironmentVariable("DEPLOY_TEST_FAILPOINT") != name) return;
                if (name == "after-evidence-before-passed-manifest") throw new PartialPrepareCrashException();
                if (name == "preflight-secret-source-stat") throw Refuse("secret-source-stat-failed");
                if (name == "preflight-required-reference-scan") throw Refuse("required-reference-scan-failed");
                throw Refuse("atomic-publication-failed");
            }, request.Json ? null : Console.WriteLine);
            return await phase.RunAsync(request, token, repository.FullName).ConfigureAwait(false);
        }
        catch (InstallerException exception) when (!exception.Message.StartsWith("preflight:", StringComparison.Ordinal))
        { throw Refuse(exception.Message.StartsWith("partial-prepare-cleanup: ", StringComparison.Ordinal) ? exception.Message[25..] : "state-or-host-invalid"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException or OverflowException)
        { throw Refuse("state-or-host-invalid"); }
    }
    public async Task<JsonObject> RunAsync(PreflightRequest request, CancellationToken token, string? repository = null)
    {
        request.Validate(); token.ThrowIfCancellationRequested();
        var inventory = await SplitHostInventory.ReadAsync(request.Inventory, request.Mode, runner, token).ConfigureAwait(false);
        Require(inventory.Schema == 8, "schema-or-value-invalid");
        try { SafeFileSystem.EnsureSafeExistingAncestors(inventory.Document.Text("productRoot")); }
        catch (InstallerException) { throw Refuse("noncanonical-product-root"); }
        boundary?.Invoke("preflight-secret-source-stat");
        _ = ExistingDownHost.ReadSecrets(inventory);
        boundary?.Invoke("preflight-required-reference-scan");
        var worktree = "clean";
        if (repository is not null)
        {
            var revision = await runner.RunAsync("git", ["-C", repository, "rev-parse", "HEAD"], token).ConfigureAwait(false);
            Require(revision.ExitCode == 0 && revision.StandardOutput.Trim() == inventory.Revision, "source-revision-mismatch");
            var dirty = await runner.RunAsync("git", ["-C", repository, "status", "--porcelain"], token).ConfigureAwait(false);
            Require(dirty.ExitCode == 0, "source-status-unavailable"); worktree = dirty.StandardOutput.Length == 0 ? "clean" : "dirty";
            Require(worktree == "clean" || inventory.Document["source"]!.Text("dirtyDisposition") == "allow-dirty", "dirty-worktree-rejected");
            var tree = await runner.RunAsync("git", ["-C", repository, "rev-parse", inventory.Revision + "^{tree}"], token).ConfigureAwait(false);
            Require(tree.ExitCode == 0 && Match(tree.StandardOutput.Trim(), "[0-9a-f]{40,64}"), "source-tree-invalid");
        }
        Require(Name(request.RunId), "unsafe-run-id");
        Require(Root(request.StateRoot) && Root(request.EvidenceRoot), "unsafe-output-root");
        using var state = new PartialPrepareState(request.StateRoot, request.EvidenceRoot, false, _ => { }, "preflight");
        PreflightPrivateUploads.Initialize(state, inventory, request.StateRoot);
        JsonObject? previous = null;
        if (state.Exists("manifest.json"))
        {
            try { previous = state.Read("manifest.json"); }
            catch (JsonException) { throw Refuse("invalid-existing-manifest"); }
            Require(previous["schemaVersion"]?.GetValue<int>() == 1 && previous.Text("runId") == request.RunId && previous.Text("mode") == request.Mode && previous.Text("inventorySha256") == inventory.Sha256 && previous["source"]!.Text("revision") == inventory.Revision && previous["source"]!.Text("worktreeState") == worktree, "resume-contract-mismatch");
            if (previous.Text("phaseStatus") == "passed")
            {
                JsonObject evidence;
                try { evidence = state.Read("preflight.json", true); }
                catch (Exception exception) when (exception is InstallerException or IOException or UnauthorizedAccessException or JsonException) { throw Refuse("completed-check-or-target-mismatch"); }
                Require(ResumeFields.All(key => JsonNode.DeepEquals(previous[key], evidence[key])), "completed-check-or-target-mismatch");
            }
        }
        var now = Now();
        var manifest = previous is null ? new JsonObject
        {
            ["schemaVersion"] = 1,
            ["runId"] = request.RunId,
            ["mode"] = request.Mode,
            ["inventorySha256"] = inventory.Sha256,
            ["source"] = new JsonObject { ["revision"] = inventory.Revision, ["dirtyDisposition"] = inventory.Document["source"]!.Text("dirtyDisposition"), ["worktreeState"] = worktree },
            ["targets"] = new JsonArray(),
            ["checks"] = new JsonArray(),
            ["startedAt"] = now,
            ["updatedAt"] = now,
            ["phaseStatus"] = "running"
        } : (JsonObject)previous.DeepClone();
        manifest["phaseStatus"] = "running"; manifest["checks"] = new JsonArray(); manifest["targets"] = new JsonArray(); manifest.Remove("completedAt");
        void PublishManifest() { boundary?.Invoke("before-manifest-rename"); state.WriteState("manifest.json", manifest); }
        PublishManifest();
        state.ClearPreflightEvidence();
        var connectivityFailed = false;
        try
        {
            void Record(string stage, string target, string check, string outcome, string reason)
            {
                var stamp = Now();
                manifest.Array("checks").Add(new JsonObject { ["stage"] = stage, ["target"] = target, ["check"] = check, ["status"] = outcome, ["reason"] = reason, ["timestamp"] = stamp });
                manifest["updatedAt"] = stamp; PublishManifest();
                status?.Invoke($"stage={stage} check={target}/{check} status={outcome} reason={reason}");
                if (stage == "connectivity" && outcome == "failed") connectivityFailed = true;
            }
            var probe = new PreflightProbe(host, Record); var targets = new JsonArray();
            foreach (var target in inventory.Targets)
            {
                token.ThrowIfCancellationRequested(); var node = Target(inventory, target.Name);
                var minimum = target.Component is null && inventory.Document["deployment"]!["services"]!.Text("mode") == "deploy"
                    ? HVO.SkyMonitor.Deployment.Contracts.PreflightHostObservation.MemoryToKiB(inventory.Document["deployment"]!["limits"]!.Text("sqlMemory"))!.Value : BigInteger.Zero;
                targets.Add(await probe.TargetAsync(target, node, minimum, token).ConfigureAwait(false));
            }
            foreach (var endpoint in inventory.Document.Array("serviceEndpoints"))
                foreach (var route in endpoint!.Array("fromTargets"))
                    probe.Connectivity(inventory.Targets.Single(t => t.Name == route!.GetValue<string>()), endpoint.Text("name"), "tcp", token, endpoint.Text("host"), endpoint!["port"]!.ToJsonString());
            foreach (var target in inventory.Targets.Where(t => t.Component == "cameraAgent"))
                if (request.Mode == "isolated") Record("connectivity", target.Name, "logic-public-authority", "passed", "deferred-until-up");
                else probe.Connectivity(target, "logic-public-authority", "http-reachable", token, inventory.Document["logicHost"]!.Text("publicEndpoint"));
            manifest["targets"] = targets; manifest["phaseStatus"] = "passed"; manifest["updatedAt"] = Now(); manifest["completedAt"] = manifest["updatedAt"]!.DeepClone();
            if (previous?.Text("phaseStatus") == "passed")
            {
                var oldTargets = previous.Array("targets").Select(n => Projection(n!, CorrelationFields)).ToArray();
                var newTargets = targets.Select(n => Projection(n!, CorrelationFields)).ToArray();
                var oldChecks = previous.Array("checks").Select(WithoutTimestamp).ToArray(); var newChecks = manifest.Array("checks").Select(WithoutTimestamp).ToArray();
                Require(JsonNode.DeepEquals(new JsonArray(oldTargets), new JsonArray(newTargets)) && JsonNode.DeepEquals(new JsonArray(oldChecks), new JsonArray(newChecks)), "completed-check-or-target-mismatch");
            }
            state.WritePreflightEvidence(Evidence(manifest)); boundary?.Invoke("after-evidence-before-passed-manifest"); PublishManifest(); return manifest;
        }
        catch (PartialPrepareCrashException) { throw; }
        catch
        {
            // Preserve the shell's non-errtrace boundary: connectivity records its failed check and exits
            // with the running manifest and cleared evidence. It never restores a prior passed receipt.
            if (connectivityFailed) throw;
            manifest["phaseStatus"] = "failed"; manifest["updatedAt"] = Now();
            try { state.WritePreflightEvidence(Evidence(manifest)); PublishManifest(); } catch (Exception exception) when (exception is InstallerException or IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
    private static JsonNode? WithoutTimestamp(JsonNode? node) { var copy = (JsonObject)node!.DeepClone(); copy.Remove("timestamp"); return copy; }
    private static JsonObject Projection(JsonNode node, string[] keys) => new(keys.Select(key => KeyValuePair.Create(key, node[key]?.DeepClone())));
    internal static JsonObject Evidence(JsonObject manifest) => Projection(manifest, EvidenceFields);
    internal static JsonNode Target(SplitHostInventory inventory, string name) => new[] { inventory.Document["logicHost"]! }.Concat(inventory.Document.Array("cameraAgents").Select(n => n!)).Concat(inventory.Document["sharedServices"] is { } shared ? [shared] : []).Single(n => n.Text("name") == name);
}
