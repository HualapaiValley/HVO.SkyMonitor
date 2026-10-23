using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment.Contracts;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment;

internal sealed record ExistingDownRequest(string Inventory, string Mode, string RunId, string StateRoot, string EvidenceRoot, string Policy, string? Confirmation, bool Json = false) : DeploymentCommand(Json)
{
    internal string? WorktreeState { get; init; }
    public void Validate()
    {
        if (!Path.IsPathFullyQualified(Inventory) || !Name(RunId) || Mode is not ("isolated" or "persistent") || !Root(StateRoot) || !Root(EvidenceRoot) || Policy is not ("preserve" or "delete")) throw new InstallUsageException("existing-down requires an inventory, mode, safe run ID, absolute state/evidence roots, and exactly one state policy.");
    }
    public static ExistingDownRequest Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal); string? policy = null; var json = false;
        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i];
            if (key == "--json" && !json) { json = true; continue; }
            if (key is "--preserve-state" or "--delete-state" && policy is null) { policy = key == "--preserve-state" ? "preserve" : "delete"; continue; }
            if (key is not ("--inventory" or "--mode" or "--run-id" or "--state-root" or "--evidence-root" or "--confirm")) throw new InstallUsageException($"Unknown option '{key}'.");
            if (i + 1 >= args.Length || !options.TryAdd(key, args[++i])) throw new InstallUsageException($"Missing value or repeated option '{key}'.");
        }
        string Need(string key) => options.GetValueOrDefault(key) ?? throw new InstallUsageException(key + " is required.");
        var run = Need("--run-id"); var output = Path.GetFullPath("TestResults/deploy/" + run);
        var request = new ExistingDownRequest(Path.GetFullPath(Need("--inventory")), Need("--mode"), run, options.GetValueOrDefault("--state-root") ?? output + "/state", options.GetValueOrDefault("--evidence-root") ?? output + "/evidence", policy ?? "", options.GetValueOrDefault("--confirm"), json); request.Validate(); return request;
    }
}

internal sealed class ExistingDown(IDeploymentHost host, IProcessInputRunner runner, string repository, Action<string>? boundary = null, int attempts = 60, TimeSpan? poll = null)
{
    private static InstallerException Refuse(string code) => new("down: " + code);
    public static async Task<JsonObject> ExecuteAsync(ExistingDownRequest request, CancellationToken token)
    {
        var root = new DirectoryInfo(Environment.CurrentDirectory);
        while (root is not null && !File.Exists(root.FullName + "/HVO.SkyMonitor.v9.slnx")) root = root.Parent;
        if (root is null) throw Refuse("source-checkout-required");
        try
        {
            var runner = new ProcessRunner();
            var revision = await runner.RunAsync("git", ["-C", root.FullName, "rev-parse", "HEAD"], token).ConfigureAwait(false);
            var document = Parse(await File.ReadAllTextAsync(request.Inventory, token).ConfigureAwait(false));
            Require(revision.ExitCode == 0 && revision.StandardOutput.Trim() == document["source"]!.Text("revision"), "source-revision-mismatch");
            var dirty = await runner.RunAsync("git", ["-C", root.FullName, "status", "--porcelain"], token).ConfigureAwait(false);
            Require(dirty.ExitCode == 0 && (document["source"]!.Text("dirtyDisposition") == "allow-dirty" || dirty.StandardOutput.Length == 0), "dirty-worktree-rejected");
            var seconds = Environment.GetEnvironmentVariable("DEPLOY_TEST_POLL_SECONDS") == "0" ? TimeSpan.Zero : TimeSpan.FromSeconds(2);
            return await new ExistingDown(new ExistingDownDockerHost(), runner, root.FullName, point =>
            {
                if (Environment.GetEnvironmentVariable("DEPLOY_TEST_FAILPOINT") != point) return;
                if (point.StartsWith("abrupt-", StringComparison.Ordinal)) throw new PartialPrepareCrashException();
                throw Refuse("injected-failure");
            }, poll: seconds).RunAsync(request with { WorktreeState = dirty.StandardOutput.Length == 0 ? "clean" : "dirty" }, token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException or InvalidOperationException) { throw Refuse("state-or-host-invalid"); }
    }
    private void Boundary(string point) => boundary?.Invoke(point);
    public async Task<JsonObject> RunAsync(ExistingDownRequest request, CancellationToken token)
    {
        request.Validate(); token.ThrowIfCancellationRequested();
        var inventory = await SplitHostInventory.ReadAsync(request.Inventory, request.Mode, runner, token).ConfigureAwait(false);
        Require(inventory.Schema == 8, "schema-or-value-invalid");
        _ = ExistingDownHost.ReadSecrets(inventory);
        foreach (var target in inventory.Targets) Require(!Beneath(request.StateRoot, target.RuntimeRoot) && !Beneath(request.EvidenceRoot, target.RuntimeRoot), "journal-inside-runtime-root");
        using var state = new PartialPrepareState(request.StateRoot, request.EvidenceRoot, false, Boundary, "down");
        var preflight = state.Read("manifest.json"); RequireMatch(preflight, inventory, request, true);
        Require(request.WorktreeState is null || preflight["source"]!.Text("worktreeState") == request.WorktreeState, "resume-contract-mismatch");
        var evidence = state.Read("preflight.json", true);
        foreach (var field in new[] { "schemaVersion", "runId", "mode", "inventorySha256", "source", "phaseStatus", "targets", "checks" }) Require(JsonNode.DeepEquals(preflight[field], evidence[field]), "completed-check-or-target-mismatch");
        var prepare = state.Read("prepare-ledger.json"); RequireMatch(prepare, inventory, request, false);
        Require(JsonNode.DeepEquals(prepare, state.Read("prepare-manifest.json")) && JsonNode.DeepEquals(PartialPrepareRecovery.PrepareEvidence(prepare), state.Read("prepare.json", true)), "prepare-evidence-mismatch");
        ValidatePrepare(prepare, inventory, request);
        RequireMatch(state.Read("up-manifest.json"), inventory, request, false);
        if (request.Policy == "delete")
        {
            if (request.Mode != "isolated" || request.Confirmation != request.RunId) throw Refuse("confirmation-mismatch");
            throw Refuse("existing-services-not-owned");
        }
        Require(inventory.Document["deployment"]!["services"]!.Text("mode") == "existing", "existing-services-mode-required");
        var remote = new ExistingDownHost(host, inventory, preflight, state, request);
        JsonObject? ledger = null;
        try
        {
            await remote.ReconcileAsync(token).ConfigureAwait(false);
            ledger = state.Recover(value => ValidateLedger(value, inventory, request)) ?? new JsonObject
            {
                ["schemaVersion"] = 2,
                ["publicationGeneration"] = 0,
                ["runId"] = request.RunId,
                ["mode"] = request.Mode,
                ["inventorySha256"] = inventory.Sha256,
                ["sourceRevision"] = inventory.Revision,
                ["policy"] = "preserve",
                ["phaseStatus"] = "running",
                ["startedAt"] = Now(),
                ["updatedAt"] = Now(),
                ["resources"] = new JsonArray()
            };
            ledger["phaseStatus"] = "running"; ledger.Remove("completedAt");
            state.PreparePrivateDirectories("down-rendered", "down-private");
            await remote.ReconcileAsync(token).ConfigureAwait(false); state.Publish(ledger);
            foreach (var target in inventory.Targets.Where(t => t.Component == "cameraAgent").Concat(inventory.Targets.Where(t => t.Component == "logicHost")))
            {
                token.ThrowIfCancellationRequested(); var resourceName = target.Component == "logicHost" ? "logichost" : "cameraagent:" + target.Name;
                var action = ledger.Array("resources").SingleOrDefault(n => n!.Text("resource") == resourceName && n.Text("action") == "graceful-stop");
                if (action?.Text("status") != "completed")
                {
                    var retainedCameraIntent = target.Component == "cameraAgent" && action is not null;
                    if (action is null) { action = Intent(resourceName, "graceful-stop"); ledger.Array("resources").Add(action); }
                    state.Publish(ledger);
                    // An intent can precede the drain or outlive a successful stop. Only live,
                    // correlated Docker evidence permits recovery without the stopped application's HTTP.
                    var alreadyStopped = retainedCameraIntent && await ComposeAsync(remote, inventory, request, target, true, token, allowRunning: true).ConfigureAwait(false);
                    if (!alreadyStopped)
                    {
                        if (target.Component == "cameraAgent") await PauseAndDrainAsync(remote, inventory, request, target, ledger["publicationGeneration"]!.GetValue<int>(), token).ConfigureAwait(false);
                        await ComposeAsync(remote, inventory, request, target, false, token).ConfigureAwait(false);
                        await ComposeAsync(remote, inventory, request, target, true, token).ConfigureAwait(false);
                    }
                    Boundary("after-graceful-stop-mutation-" + resourceName);
                    Complete(action); state.Publish(ledger); Boundary("after-graceful-stop-" + resourceName);
                }
                else await ComposeAsync(remote, inventory, request, target, true, token).ConfigureAwait(false);
            }
            var preserve = ledger.Array("resources").SingleOrDefault(n => n!.Text("resource") == "existing-services" && n.Text("action") == "preserve");
            if (preserve?.Text("status") != "completed")
            {
                if (preserve is null) { preserve = Intent("existing-services", "preserve"); ledger.Array("resources").Add(preserve); }
                state.Publish(ledger); Complete(preserve); state.Publish(ledger); Boundary("after-preserve-existing-services");
            }
            await remote.ReconcileAsync(token).ConfigureAwait(false); Boundary("abrupt-after-down-cleanup");
            token.ThrowIfCancellationRequested(); ledger["phaseStatus"] = "passed"; ledger["completedAt"] = Now(); state.Publish(ledger); return ledger;
        }
        catch (Exception e) when (e is not (PartialPrepareCrashException or OutOfMemoryException))
        {
            if (ledger is not null && !state.PublicationInProgress)
            {
                ledger["phaseStatus"] = "failed"; ledger.Remove("completedAt");
                try { state.Publish(ledger); state.Publish(ledger); } catch (Exception publication) when (publication is InstallerException or IOException or UnauthorizedAccessException) { }
            }
            // Cancellation does not cancel the bounded obligation to erase credentials; failed entries remain resumable.
            await BestEffortCleanupAsync(remote).ConfigureAwait(false);
            throw;
        }
    }
    private static async Task BestEffortCleanupAsync(ExistingDownHost remote)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await remote.ReconcileAsync(cleanup.Token).ConfigureAwait(false); } catch (Exception failure) when (failure is InstallerException or IOException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException or FormatException or OverflowException) { }
    }
    private static void RequireMatch(JsonObject value, SplitHostInventory inventory, ExistingDownRequest request, bool preflight)
    {
        Require(value["schemaVersion"]!.GetValue<int>() == 1 && value.Text("runId") == request.RunId && value.Text("mode") == request.Mode && value.Text("inventorySha256") == inventory.Sha256 && (preflight ? value["source"]!.Text("revision") : value.Text("sourceRevision")) == inventory.Revision && value.Text("phaseStatus") == "passed", "mismatch-or-not-passed");
    }
    private static void ValidatePrepare(JsonObject prepare, SplitHostInventory inventory, ExistingDownRequest request)
    {
        var prepared = PartialPrepareRecovery.ValidatePrepare(inventory, request.RunId, prepare, passed: true);
        var expected = inventory.Targets.Where(t => t.Component is not null || inventory.Document["deployment"]!["services"]!.Text("mode") == "deploy").Select(t => t.Name);
        Require(prepared.Select(t => t.Target).ToHashSet(StringComparer.Ordinal).SetEquals(expected), "prepare-target-mismatch");
    }
    private static void ValidateLedger(JsonObject value, SplitHostInventory inventory, ExistingDownRequest request)
    {
        Keys(value, "schemaVersion publicationGeneration runId mode inventorySha256 sourceRevision policy phaseStatus startedAt updatedAt resources", "completedAt");
        Require(value["schemaVersion"]!.GetValue<int>() == 2 && value["publicationGeneration"]!.GetValue<int>() >= 1 && value.Text("runId") == request.RunId && value.Text("mode") == request.Mode && value.Text("inventorySha256") == inventory.Sha256 && value.Text("sourceRevision") == inventory.Revision && value.Text("policy") == request.Policy && value.Text("phaseStatus") is "running" or "failed" or "passed", "down-ledger-invalid");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in value.Array("resources"))
        {
            Keys(resource!, "resource action status intentAt updatedAt", "completedAt"); Require(seen.Add(resource!.Text("resource") + ":" + resource.Text("action")) && resource.Text("status") is "intent" or "completed", "down-ledger-invalid");
            Require(resource.Text("action") == "graceful-stop" && (resource.Text("resource") == "logichost" || inventory.Targets.Any(t => t.Component == "cameraAgent" && resource.Text("resource") == "cameraagent:" + t.Name)) || resource.Text("action") == "preserve" && resource.Text("resource") == "existing-services", "down-ledger-invalid");
        }
    }
    private static JsonObject Intent(string resource, string action) => new() { ["resource"] = resource, ["action"] = action, ["status"] = "intent", ["intentAt"] = Now(), ["updatedAt"] = Now() };
    private static void Complete(JsonNode action) { action["status"] = "completed"; action["completedAt"] = Now(); action["updatedAt"] = Now(); }
    private async Task<bool> ComposeAsync(ExistingDownHost remote, SplitHostInventory inventory, ExistingDownRequest request, DeploymentTarget target, bool inspect, CancellationToken token, bool allowRunning = false)
    {
        var endpoint = await remote.CorrelateAsync(target, token).ConfigureAwait(false); var service = target.Component == "logicHost" ? "logichost" : "cameraagent"; var project = inventory.Project(target);
        var env = request.StateRoot + "/up-rendered/" + target.Name + ".env"; _ = PartialPrepareState.ReadPrivate(env);
        string[] compose = ["--host", endpoint, "compose", "--project-name", project, "--env-file", env, "--file", repository + "/deploy/split-host/compose." + service + ".yml"];
        var result = await host.DockerAsync([.. compose, .. (inspect ? new[] { "ps", "--all", "-q", service } : ["stop", "-t", "60", service])], token).ConfigureAwait(false);
        Require(result.ExitCode == 0, "compose-failed");
        var stopped = inspect;
        if (inspect && result.StandardOutput.Trim() is { Length: > 0 } container)
        {
            Require(Match(container, "[a-f0-9]{12,64}"), "container-identity-invalid");
            var identity = await host.DockerAsync(["--host", endpoint, "container", "inspect", "--format", "{\"project\":{{json (index .Config.Labels \"com.docker.compose.project\")}},\"service\":{{json (index .Config.Labels \"com.docker.compose.service\")}},\"running\":{{json .State.Running}}}", container], token).ConfigureAwait(false);
            Require(identity.ExitCode == 0, "container-state-unavailable"); var doc = Parse(identity.StandardOutput);
            stopped = !doc.Flag("running");
            Require(doc.Text("project") == project && doc.Text("service") == service && (allowRunning || stopped), "completed-container-state-invalid");
        }
        await remote.CorrelateAsync(target, token).ConfigureAwait(false);
        return stopped;
    }
    private async Task PauseAndDrainAsync(ExistingDownHost remote, SplitHostInventory inventory, ExistingDownRequest request, DeploymentTarget target, int generation, CancellationToken token)
    {
        var root = target.RuntimeRoot + "/.hvo-deploy/down-" + request.RunId;
        await remote.EnsureDirectoryAsync(target, root, token).ConfigureAwait(false);
        var headers = await remote.LoginAsync(target, root, token).ConfigureAwait(false);
        var endpoint = inventory.Document.Array("cameraAgents").Single(n => n!.Text("name") == target.Name)!.Text("internalEndpoint");
        async Task<JsonObject> Read(string suffix, string name, bool post = false, string? content = null, string? key = null)
        {
            var body = post ? root + "/" + name.Replace("-response", "", StringComparison.Ordinal) + ".json" : null;
            var headerPath = key is not null ? root + "/" + key + "-" + generation + "-control.headers" : null;
            if (body is not null)
            {
                SafeFileSystem.WriteTextAtomic(request.StateRoot + "/down-rendered/" + target.Name + "-" + name.Replace("-response", "", StringComparison.Ordinal) + ".json", content! + "\n");
                await remote.WriteAsync(target, body, content! + "\n", token).ConfigureAwait(false);
            }
            if (headerPath is not null) await remote.WriteAsync(target, headerPath, headers + "Idempotency-Key: deploy-down-" + request.RunId + "-" + target.Name + "-" + key + "-" + generation + "\n", token).ConfigureAwait(false);
            var response = await remote.HttpAsync(target, post ? "POST" : "GET", endpoint + suffix, root + "/" + name + ".json", root + "/owner.cookies", body, headerPath, token).ConfigureAwait(false);
            SafeFileSystem.WriteTextAtomic(request.StateRoot + "/down-private/" + target.Name + "-" + name + ".json", response.Body);
            if (response.Status != 200) throw Refuse(post ? key == "pause" ? "unexpected-pause-status" : "unexpected-final-pause-status" : "unexpected-observation-status");
            var parsed = ExistingDownHost.NormalizeResponse(response.Body);
            SafeFileSystem.WriteTextAtomic(request.StateRoot + "/down-private/" + target.Name + "-" + name + ".json", parsed.ToJsonString(Options) + "\n");
            return parsed;
        }
        var pause = DownObservation.Pause(await Read("/api/v1/operations/capture/pause", "pause-response", true, "{\"reason\":\"split-host graceful shutdown\"}", "pause").ConfigureAwait(false));
        var boundaryValue = DownObservation.Continuity(await Read("/api/internal/deployment/continuity?downContinuity=" + generation + "-boundary", "continuity-boundary").ConfigureAwait(false));
        var mode = inventory.Document["deployment"]!["transient"]!.Text("mode");
        DownSample? accepted = null; var consecutive = 0; var observation = 0;
        for (observation = 1; observation <= attempts; observation++)
        {
            token.ThrowIfCancellationRequested();
            var summary = await Read("/api/v1/operations/summary?downObservation=" + generation + "-" + observation, "summary-" + observation).ConfigureAwait(false);
            var continuity = DownObservation.Continuity(await Read("/api/internal/deployment/continuity?downContinuity=" + generation + "-" + observation, "continuity-" + observation).ConfigureAwait(false), boundaryValue.Device);
            if (continuity != boundaryValue) throw Refuse("continuity-boundary-changed");
            var sample = DownObservation.Safe(summary, pause, boundaryValue, mode);
            if (sample is null || accepted is not null && sample.Captured <= accepted.Captured) { accepted = null; consecutive = 0; }
            else { consecutive = accepted?.Fingerprint == sample.Fingerprint ? consecutive + 1 : 1; accepted = sample; }
            if (consecutive >= 2) break;
            await Task.Delay(poll ?? TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        }
        if (consecutive < 2 || accepted is null) throw Refuse("drain-timeout");
        DownPause final;
        try { final = DownObservation.Pause(await Read("/api/v1/operations/capture/pause", "final-pause-response", true, "{\"reason\":\"split-host graceful shutdown final reassertion\"}", "final-pause").ConfigureAwait(false), pause.Version); }
        catch (InstallerException) { throw Refuse("final-pause-version-mismatch"); }
        observation++;
        var last = await Read("/api/v1/operations/summary?downObservation=" + generation + "-final-" + observation, "summary-" + observation).ConfigureAwait(false);
        var finalBoundary = DownObservation.Continuity(await Read("/api/internal/deployment/continuity?downContinuity=" + generation + "-final-" + observation, "continuity-" + observation).ConfigureAwait(false), boundaryValue.Device);
        if (finalBoundary != boundaryValue) throw Refuse("final-continuity-boundary-changed");
        var proof = DownObservation.Safe(last, final, boundaryValue, mode);
        if (proof is null) throw Refuse("final-observation-unsafe");
        if (proof.Captured <= accepted.Captured || proof.Fingerprint != accepted.Fingerprint) throw Refuse("final-observation-changed");
        await remote.ReconcileAsync(token).ConfigureAwait(false);
    }
}
