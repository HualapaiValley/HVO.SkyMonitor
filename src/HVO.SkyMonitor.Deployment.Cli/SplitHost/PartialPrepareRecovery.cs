using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment.Contracts;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment;

internal sealed record PartialPrepareRequest(string Inventory, string Mode, string RunId, string StateRoot, string EvidenceRoot, bool DryRun, string? Confirmation, bool Json = false) : DeploymentCommand(Json)
{
    public void Validate()
    {
        if (!Name(RunId) || Mode is not ("isolated" or "persistent") || !Root(StateRoot) || !Root(EvidenceRoot) || !Path.IsPathFullyQualified(Inventory)) throw new InstallUsageException("Recovery requires an inventory, mode, safe run ID, and absolute state/evidence roots.");
        if (DryRun ? Confirmation is not null : Confirmation is null) throw new InstallUsageException("Specify --dry-run or --confirm RUN-ID.");
    }
    public static PartialPrepareRequest Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal); var dry = false; var json = false;
        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i];
            if (key == "--dry-run" && !dry) { dry = true; continue; }
            if (key == "--json" && !json) { json = true; continue; }
            if (key is not ("--inventory" or "--mode" or "--run-id" or "--state-root" or "--evidence-root" or "--confirm") || i + 1 >= args.Length || !options.TryAdd(key, args[++i])) throw new InstallUsageException("Invalid or repeated recovery option.");
        }
        string Need(string key) => options.GetValueOrDefault(key) ?? throw new InstallUsageException(key + " is required.");
        var run = Need("--run-id"); var output = Path.GetFullPath("TestResults/deploy/" + run);
        var request = new PartialPrepareRequest(Need("--inventory"), Need("--mode"), run, options.GetValueOrDefault("--state-root") ?? output + "/state", options.GetValueOrDefault("--evidence-root") ?? output + "/evidence", dry, options.GetValueOrDefault("--confirm"), json);
        request.Validate(); return request;
    }
}
internal sealed class PartialPrepareCrashException : Exception
{
    public PartialPrepareCrashException() { }
    public PartialPrepareCrashException(string message) : base(message) { }
    public PartialPrepareCrashException(string message, Exception innerException) : base(message, innerException) { }
}
internal sealed class PartialPrepareRecovery(IDeploymentHost host, IProcessRunner runner, Action<string>? injectedBoundary = null)
{
    private static readonly string[] PrepareEvidenceKeys = ["schemaVersion", "runId", "mode", "inventorySha256", "phaseStatus", "startedAt", "updatedAt", "completedAt"];
    private static readonly string[] PrepareTargetKeys = ["target", "status", "disposition", "controlDirectory", "markerDigest", "creatingRunId"];
    private static readonly string[] AllContainers = ["--all"];

    public static async Task<JsonObject> ExecuteAsync(PartialPrepareRequest request, CancellationToken token)
    {
        try
        {
            return await new PartialPrepareRecovery(new DeploymentHost(), new ProcessRunner(), value =>
            {
                if (Environment.GetEnvironmentVariable("DEPLOY_TEST_FAILPOINT") != value) return;
                if (value.StartsWith("abrupt-", StringComparison.Ordinal)) throw new PartialPrepareCrashException();
                throw Error("injected-failure");
            }).RunAsync(request, token).ConfigureAwait(false);
        }
        catch (InstallerException exception) when (!exception.Message.StartsWith("partial-prepare-cleanup: ", StringComparison.Ordinal)) { throw Error("state-or-host-invalid"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException or InvalidOperationException) { throw Error("state-or-host-invalid"); }
    }
    public async Task<JsonObject> RunAsync(PartialPrepareRequest request, CancellationToken token)
    {
        request.Validate(); token.ThrowIfCancellationRequested(); Require(request.DryRun || request.Confirmation == request.RunId, "confirmation-mismatch");
        var inventory = await SplitHostInventory.ReadAsync(request.Inventory, request.Mode, runner, token).ConfigureAwait(false);
        foreach (var target in inventory.Targets) Require(!Beneath(request.StateRoot, target.RuntimeRoot) && !Beneath(request.EvidenceRoot, target.RuntimeRoot), "journal-inside-runtime-root");
        using var state = new PartialPrepareState(request.StateRoot, request.EvidenceRoot, request.DryRun, Boundary);
        var preflight = state.Read("manifest.json");
        Require(preflight["schemaVersion"]?.GetValue<int>() == 1 && preflight.Text("runId") == request.RunId && preflight.Text("mode") == request.Mode && preflight.Text("inventorySha256") == inventory.Sha256 && preflight["source"]!.Text("revision") == inventory.Revision && preflight.Text("phaseStatus") == "passed", "mismatch-or-not-passed");
        var preflightEvidence = state.Read("preflight.json", true);
        foreach (var field in new[] { "schemaVersion", "runId", "mode", "inventorySha256", "source", "phaseStatus", "targets", "checks" }) Require(JsonNode.DeepEquals(preflight[field], preflightEvidence[field]), "completed-check-or-target-mismatch");
        var prepare = state.Read("prepare-ledger.json"); var prepared = ValidatePrepare(inventory, request.RunId, prepare);
        Require(JsonNode.DeepEquals(prepare, state.Read("prepare-manifest.json")), "manifest-mismatch");
        Require(JsonNode.DeepEquals(PrepareEvidence(prepare), state.Read("prepare.json", true)), "evidence-mismatch");
        state.RequireNoLaterPhase();
        var projection = Projection(inventory, prepared, request.RunId);
        JsonObject? ledger = null;
        try
        {
            ledger = state.Recover(value => ValidateJournal(value, inventory, request, projection)) ?? new JsonObject
            {
                ["schemaVersion"] = 1, ["publicationGeneration"] = 0, ["runId"] = request.RunId, ["mode"] = request.Mode,
                ["inventorySha256"] = inventory.Sha256, ["sourceRevision"] = inventory.Revision, ["operation"] = request.DryRun ? "dry-run" : "cleanup",
                ["phaseStatus"] = "running", ["startedAt"] = Now(), ["updatedAt"] = Now(), ["targets"] = projection, ["resources"] = new JsonArray()
            };
            ledger["phaseStatus"] = "running"; ledger.Remove("completedAt");
            await VerifyAllAsync(inventory, request, preflight, prepared, ledger, token).ConfigureAwait(false);
            state.Publish(ledger);
            if (!request.DryRun)
            {
                foreach (var target in inventory.Targets)
                {
                    var entry = prepared.SingleOrDefault(p => p.Target == target.Name && p.CreatingRunId == request.RunId); if (entry is null) continue;
                    foreach (var action in new[] { "cleanup-runtime-artifacts", "cleanup-prepare-lock" })
                    {
                        token.ThrowIfCancellationRequested();
                        var resource = ledger.Array("resources").SingleOrDefault(n => n!.Text("resource") == "target:" + target.Name && n.Text("action") == action);
                        if (resource?.Text("status") == "completed") continue;
                        if (resource is null)
                        {
                            resource = new JsonObject { ["resource"] = "target:" + target.Name, ["action"] = action, ["status"] = "intent", ["intentAt"] = Now(), ["updatedAt"] = Now() };
                            ledger.Array("resources").Add(resource);
                        }
                        state.Publish(ledger);
                        await VerifyDockerAsync(inventory, request.RunId, preflight, token).ConfigureAwait(false);
                        using (var files = host.Open(target, token))
                        {
                            new PartialPrepareTarget(files, target).Execute(entry, Status(ledger, target.Name, "cleanup-runtime-artifacts"), Status(ledger, target.Name, "cleanup-prepare-lock"),
                                action == "cleanup-runtime-artifacts" ? "cleanup-root" : "cleanup-lock", name => Boundary("after-partial-prepare-" + name + ":" + target.Name));
                        }
                        // Correlate again before recording completion; a disconnect never turns intent into success.
                        using (var files = host.Open(target, token)) new PartialPrepareTarget(files, target).Authenticate();
                        resource["status"] = "completed"; resource["completedAt"] = Now(); resource["updatedAt"] = Now(); state.Publish(ledger);
                    }
                }
                await VerifyAllAsync(inventory, request, preflight, prepared, ledger, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested(); ledger["phaseStatus"] = "passed"; ledger["completedAt"] = Now(); state.Publish(ledger); return ledger;
        }
        catch (Exception exception) when (exception is not (PartialPrepareCrashException or OutOfMemoryException))
        {
            if (ledger is not null)
            {
                ledger["phaseStatus"] = "failed"; ledger.Remove("completedAt");
                try { state.Publish(ledger); } catch (Exception publication) when (publication is InstallerException or IOException or UnauthorizedAccessException) { /* Preserve the original failure; ledger publication is resumable. */ }
            }
            throw;
        }
    }
    private void Boundary(string name) => injectedBoundary?.Invoke(name);
    internal static JsonObject PrepareEvidence(JsonObject prepare)
    {
        var evidence = new JsonObject();
        foreach (var field in PrepareEvidenceKeys) evidence[field] = prepare[field]?.DeepClone();
        evidence["targets"] = new JsonArray(prepare.Array("targets").Select(entry =>
        {
            var value = new JsonObject(); foreach (var field in PrepareTargetKeys) value[field] = entry![field]!.DeepClone(); return (JsonNode)value;
        }).ToArray()); return evidence;
    }
    private static List<PreparedTarget> ValidatePrepare(SplitHostInventory inventory, string run, JsonObject prepare)
    {
        Keys(prepare, "schemaVersion runId mode inventorySha256 sourceRevision phaseStatus startedAt updatedAt targets", "completedAt");
        Require(prepare["schemaVersion"]?.GetValue<int>() == 1 && prepare.Text("runId") == run && prepare.Text("mode") == inventory.Mode && prepare.Text("inventorySha256") == inventory.Sha256 && prepare.Text("sourceRevision") == inventory.Revision, "prepare-ledger-invalid");
        Require(prepare.Text("phaseStatus") == "failed" && !prepare.ContainsKey("completedAt"), "failed-state-required");
        Require(prepare.Text("startedAt").Length > 0 && prepare.Text("updatedAt").Length > 0, "prepare-ledger-invalid");
        var entries = new List<PreparedTarget>();
        foreach (var entry in prepare.Array("targets"))
        {
            Keys(entry!, "target runtimeRoot disposition newlyCreated controlDirectory markerDigest creatingRunId status"); Keys(entry!["newlyCreated"]!, "root controlDirectory marker");
            var target = inventory.Targets.SingleOrDefault(t => t.Name == entry.Text("target")) ?? throw Error("target-mismatch");
            var flags = new PrepareCreation(entry["newlyCreated"]!.Flag("root"), entry["newlyCreated"]!.Flag("controlDirectory"), entry["newlyCreated"]!.Flag("marker"));
            Require(entry.Text("runtimeRoot") == target.RuntimeRoot && entry.Text("markerDigest") == inventory.Marker(target, run) && entry.Text("controlDirectory") == ".hvo-deploy" && entry.Text("status") == "prepared" && Name(entry.Text("creatingRunId")), "prepare-ledger-invalid");
            var own = entry.Text("creatingRunId") == run;
            Require(own ? entry.Text("disposition") == (flags.Root ? "newly-created" : "preexisting-or-resumed") && (flags.Root || flags.ControlDirectory || flags.Marker) : inventory.Mode == "persistent" && entry.Text("disposition") == "preexisting-or-resumed" && flags == new PrepareCreation(false, false, false), "invalid-creation-flags");
            Require(entries.All(e => e.Target != target.Name), "duplicate-target");
            entries.Add(new PreparedTarget(target.Name, target.RuntimeRoot, entry.Text("disposition"), flags, entry.Text("markerDigest"), entry.Text("creatingRunId")));
        }
        Require(entries.Count > 0, "no-created-targets"); return entries;
    }
    private static JsonArray Projection(SplitHostInventory inventory, IReadOnlyList<PreparedTarget> prepared, string run) => new(inventory.Targets.Select(target =>
    {
        var entry = prepared.SingleOrDefault(e => e.Target == target.Name); var flags = entry?.NewlyCreated ?? new PrepareCreation(false, false, false);
        return (JsonNode)new JsonObject { ["target"] = target.Name, ["disposition"] = entry is null ? "not-created" : entry.CreatingRunId == run ? "cleanup-candidate" : "preserved", ["newlyCreated"] = new JsonObject { ["root"] = flags.Root, ["controlDirectory"] = flags.ControlDirectory, ["marker"] = flags.Marker }, ["status"] = "verified" };
    }).ToArray());
    private static void ValidateJournal(JsonObject ledger, SplitHostInventory inventory, PartialPrepareRequest request, JsonArray projection)
    {
        Keys(ledger, "schemaVersion publicationGeneration runId mode inventorySha256 sourceRevision operation phaseStatus startedAt updatedAt targets resources", "completedAt");
        Require(ledger["schemaVersion"]?.GetValue<int>() == 1 && ledger["publicationGeneration"]!.GetValue<int>() >= 1 && ledger.Text("runId") == request.RunId && ledger.Text("mode") == request.Mode && ledger.Text("inventorySha256") == inventory.Sha256 && ledger.Text("sourceRevision") == inventory.Revision && ledger.Text("operation") == (request.DryRun ? "dry-run" : "cleanup") && ledger.Text("phaseStatus") is "running" or "failed" or "passed", "journal-invalid");
        Require(JsonNode.DeepEquals(ledger["targets"], projection), "journal-target-mismatch");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in ledger.Array("resources"))
        {
            Keys(resource!, "resource action status intentAt updatedAt", "completedAt");
            Require(keys.Add(resource!.Text("resource") + ":" + resource.Text("action")) && projection.Any(t => "target:" + t!.Text("target") == resource.Text("resource") && t.Text("disposition") == "cleanup-candidate") && resource.Text("action") is "cleanup-runtime-artifacts" or "cleanup-prepare-lock" && resource.Text("status") is "intent" or "completed", "journal-invalid");
        }
        Require(!request.DryRun || ledger.Array("resources").Count == 0, "journal-invalid");
    }
    private static string Status(JsonObject ledger, string name, string action) => ledger.Array("resources").SingleOrDefault(r => r!.Text("resource") == "target:" + name && r.Text("action") == action)?.Text("status") ?? "none";
    private async Task VerifyAllAsync(SplitHostInventory inventory, PartialPrepareRequest request, JsonObject preflight, IReadOnlyList<PreparedTarget> prepared, JsonObject ledger, CancellationToken token)
    {
        await VerifyDockerAsync(inventory, request.RunId, preflight, token).ConfigureAwait(false);
        foreach (var target in inventory.Targets)
        {
            token.ThrowIfCancellationRequested(); using var files = host.Open(target, token); var remote = new PartialPrepareTarget(files, target);
            var entry = prepared.SingleOrDefault(p => p.Target == target.Name);
            if (entry is null) remote.Unprepared(preflight.Array("targets").Single(n => n!.Text("name") == target.Name)!.Text("deploymentState"));
            else if (entry.CreatingRunId != request.RunId) remote.Preserved(entry);
            else remote.Execute(entry, Status(ledger, target.Name, "cleanup-runtime-artifacts"), Status(ledger, target.Name, "cleanup-prepare-lock"), "verify", _ => { });
        }
    }
    private async Task VerifyDockerAsync(SplitHostInventory inventory, string run, JsonObject preflight, CancellationToken token)
    {
        var endpoints = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal); var contexts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var target in inventory.Targets)
        {
            var binding = await DockerJsonAsync(["context", "inspect", target.DockerContext, "--format", "{\"name\":{{json .Name}},\"host\":{{json .Endpoints.docker.Host}}}"], token).ConfigureAwait(false);
            Require(binding.Text("name") == target.DockerContext, "docker-context-mismatch"); var endpoint = binding.Text("host");
            Require(endpoint.StartsWith("ssh://", StringComparison.Ordinal) || endpoint.StartsWith("unix://", StringComparison.Ordinal), "docker-context-invalid-endpoint");
            contexts[target.DockerContext] = endpoint;
            var retained = preflight.Array("targets").SingleOrDefault(n => n!.Text("name") == target.Name);
            Require(retained is not null && retained.Text("dockerDaemonIdentity") == target.DockerDaemonIdentity && retained.Text("hostName") == target.HostName && retained.Text("architecture") == target.Architecture && retained.Text("hostIdentity") == target.HostIdentity, "preflight-correlation-mismatch");
            var info = await DockerJsonAsync(["--host", endpoint, "info", "--format", "{\"ID\":{{json .ID}},\"Name\":{{json .Name}},\"Architecture\":{{json .Architecture}},\"OSType\":{{json .OSType}},\"ServerVersion\":{{json .ServerVersion}}}"], token).ConfigureAwait(false);
            Require(info.Text("ID") == target.DockerDaemonIdentity && info.Text("Name") == target.HostName && info.Text("OSType") == "linux" && info.Text("ServerVersion").Length > 0 && PartialPrepareTarget.Architecture(info.Text("Architecture")) == target.Architecture, "docker-daemon-correlation-mismatch");
            if (!endpoints.TryGetValue(endpoint, out var projects)) endpoints.Add(endpoint, projects = new HashSet<string>(StringComparer.Ordinal));
            if (target.Component is null) continue;
            projects.Add(inventory.Project(target));
            if (inventory.Schema == 8)
            {
                var container = "hvo-skymonitor-" + target.InstanceId!.Replace("-", "", StringComparison.Ordinal);
                await RequireAbsentAsync(endpoint, "container", container, token).ConfigureAwait(false);
                if (target.Component == "logicHost") await RequireAbsentAsync(endpoint, "container", container + "-init", token).ConfigureAwait(false);
            }
            await RequireAbsentAsync(endpoint, "network", inventory.Project(target) + "_default", token).ConfigureAwait(false);
        }
        if (inventory.Document["deployment"]!["services"]!.Text("mode") == "deploy") endpoints[contexts[inventory.Document["sharedServices"]!.Text("dockerContext")]].Add(inventory.Document["deployment"]!["resources"]!.Text("project") + "-services");
        foreach (var endpoint in endpoints)
        foreach (var filter in endpoint.Value.Select(project => "label=com.docker.compose.project=" + project).Append("label=io.hvoskymonitor.run-id=" + run))
        foreach (var kind in new[] { "container", "network", "volume" })
        {
            var result = await host.DockerAsync(["--host", endpoint.Key, kind, "ls", .. (kind == "container" ? AllContainers : []), "--quiet", "--filter", filter], token).ConfigureAwait(false);
            Require(result.ExitCode == 0 && result.StandardOutput.Trim().Length == 0, "unexpected-deployment-resource");
        }
    }
    private async Task<JsonObject> DockerJsonAsync(string[] args, CancellationToken token)
    {
        var result = await host.DockerAsync(args, token).ConfigureAwait(false); Require(result.ExitCode == 0, "docker-unavailable"); return Parse(result.StandardOutput);
    }
    private async Task RequireAbsentAsync(string endpoint, string kind, string name, CancellationToken token)
    {
        var result = await host.DockerAsync(["--host", endpoint, kind, "inspect", "--format", "{{json .Name}}", name], token).ConfigureAwait(false);
        Require(result.ExitCode == 1 && result.StandardOutput.Length == 0 && (result.StandardError.Trim() == "Error: No such " + kind + ": " + name || result.StandardError.Trim() == "Error response from daemon: No such " + kind + ": " + name || result.StandardError.Trim() == "Error: No such object: " + name), "unexpected-deployment-resource");
    }
}
