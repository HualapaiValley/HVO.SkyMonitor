using System.Text;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment.Contracts;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>A disposable typed host with real Linux files/locks; only identities and Docker are simulated.</summary>
internal sealed class DeploymentFakeHost : IDeploymentHost, IDisposable
{
    private static readonly string[] LegacyTargetKeys = ["instanceId", "applicationIdentity", "catalogId", "cookieName"];
    private static readonly string[] LegacyCatalogKeys = ["kind", "version", "sha256", "length", "rowCount"];
    private static readonly string[] LegacyInstallKeys = ["bundlePath", "installRoot", "allowFixture"];
    public static string Read(string path) => File.ReadAllText(path);
    public static void Append(string path, string text) => File.AppendAllText(path, text);

    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("hvo-1588-").FullName;
    public string LogicalRoot { get; private set; } = "/tmp/hvo-deploy-test.typed-" + Guid.NewGuid().ToString("N");
    private bool replay;
    public string InventoryPath => Directory + "/inventory.json";
    public string StateRoot => Directory + "/state";
    public string EvidenceRoot => Directory + "/evidence";
    public JsonObject Inventory { get; private set; }
    public SplitHostInventory Contract { get; private set; } = null!;
    public IReadOnlyList<PreparedTarget> Prepared { get; private set; } = [];
    public string RunId { get; set; } = "partial-cleanup";
    public string Mode { get; set; } = "isolated";
    public bool UseShellPrimitives { get; set; }
    public string? BadHost { get; set; }
    public string? BadDaemon { get; set; }
    public string? Resource { get; set; }
    public int ResourceAfter { get; set; } = 1;
    public int ResourceProbes { get; private set; }
    public string? PresentContainer { get; set; }
    public string? PresentNetwork { get; set; }
    public HashSet<string> DockerResources { get; } = new(StringComparer.Ordinal);
    public Action<string, string[]>? BeforePrimitive { get; set; }
    public Action<string, string[]>? AfterPrimitive { get; set; }
    public List<string[]> DockerCalls { get; } = [];
    public DeploymentFakeHost()
    {
        File.SetUnixFileMode(Directory, SafeFileSystem.OwnerDirectoryMode);
        using var stream = typeof(DeploymentFakeHost).Assembly.GetManifestResourceStream("HVO.SkyMonitor.Deployment.Cli.Tests.PartialPrepareInventory.json")!;
        using var reader = new StreamReader(stream); Inventory = Parse(reader.ReadToEnd());
        SetProductRoot(LogicalRoot + "/remote/skymonitor");
        foreach (var node in Targets()) node["runtimeOwner"] = NativeLinux.UserName(NativeLinux.getuid());
    }
    public void SetProductRoot(string root)
    {
        Inventory["productRoot"] = root;
        Inventory["logicHost"]!["runtimeRoot"] = root + "/logichosts/" + Inventory["logicHost"]!.Text("instanceId");
        foreach (var node in Inventory.Array("cameraAgents")) node!["runtimeRoot"] = root + "/cameraagents/" + node.Text("instanceId");
        foreach (var catalog in Inventory.Array("catalogs")) catalog!["installRoot"] = root + "/catalogs/" + catalog.Text("catalogId");
    }
    private IEnumerable<JsonNode> Targets() => new[] { Inventory["logicHost"]! }.Concat(Inventory.Array("cameraAgents").Select(n => n!)).Concat(Inventory["sharedServices"] is { } shared ? [shared] : []);
    public string Physical(string path) => path.StartsWith(LogicalRoot, StringComparison.Ordinal) ? Directory + (replay ? "" : "/remote") + path[LogicalRoot.Length..] : path;
    public async Task StageAsync(bool preexisting = false, bool foreign = false, bool legacy = false)
    {
        if (Mode == "persistent")
            foreach (var node in Targets().Where(n => n["publicEndpoint"] is not null)) { node["publicEndpoint"] = node.Text("publicEndpoint").Replace("http:", "https:", StringComparison.Ordinal); node["trustedProxyAddresses"] = new JsonArray("127.0.0.1"); }
        if (legacy) Legacy();
        Write(InventoryPath, Inventory.ToJsonString(Options));
        Contract = await SplitHostInventory.ReadAsync(InventoryPath, Mode, new ProcessRunner(), CancellationToken.None);
        System.IO.Directory.CreateDirectory(StateRoot, SafeFileSystem.OwnerDirectoryMode); System.IO.Directory.CreateDirectory(EvidenceRoot, SafeFileSystem.OwnerDirectoryMode);
        foreach (var target in Contract.Targets) System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Physical(target.RuntimeRoot))!, SafeFileSystem.OwnerDirectoryMode);
        var target0 = Contract.Targets[0]; var flags = new PrepareCreation(!preexisting, true, true);
        var prepared = new PreparedTarget(target0.Name, target0.RuntimeRoot, preexisting || foreign ? "preexisting-or-resumed" : "newly-created", foreign ? new PrepareCreation(false, false, false) : flags, Contract.Marker(target0, RunId), foreign ? "partial-foreign-old" : RunId);
        Prepared = [prepared];
        System.IO.Directory.CreateDirectory(Physical(target0.RuntimeRoot) + "/.hvo-deploy", SafeFileSystem.OwnerDirectoryMode);
        File.SetUnixFileMode(Physical(target0.RuntimeRoot), SafeFileSystem.OwnerDirectoryMode);
        Write(Physical(target0.RuntimeRoot) + "/.hvo-deploy/ownership", PartialPrepareTarget.Marker(prepared.MarkerDigest));
        var lockPath = Physical(Path.GetDirectoryName(target0.RuntimeRoot) + "/" + SplitHostInventory.LockName(target0));
        Write(lockPath, PartialPrepareTarget.Lock(prepared.MarkerDigest));
        Write(lockPath + ".state", PartialPrepareTarget.State(foreign ? prepared with { NewlyCreated = flags } : prepared));
        var now = "2026-10-23T00:00:00Z";
        var preflight = new JsonObject { ["schemaVersion"] = 1, ["runId"] = RunId, ["mode"] = Mode, ["inventorySha256"] = Contract.Sha256, ["source"] = new JsonObject { ["revision"] = Contract.Revision, ["worktreeState"] = "clean", ["dirtyDisposition"] = "allow-dirty" }, ["phaseStatus"] = "passed", ["startedAt"] = now, ["updatedAt"] = now, ["completedAt"] = now, ["checks"] = new JsonArray(), ["targets"] = new JsonArray(Contract.Targets.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["hostIdentity"] = t.HostIdentity, ["hostName"] = t.HostName, ["architecture"] = t.Architecture, ["dockerDaemonIdentity"] = t.DockerDaemonIdentity, ["deploymentState"] = "needs_prepare" }).ToArray()) };
        WriteJson(StateRoot + "/manifest.json", preflight); WriteJson(EvidenceRoot + "/preflight.json", preflight);
        var ledger = new JsonObject { ["schemaVersion"] = 1, ["runId"] = RunId, ["mode"] = Mode, ["inventorySha256"] = Contract.Sha256, ["sourceRevision"] = Contract.Revision, ["phaseStatus"] = "failed", ["startedAt"] = now, ["updatedAt"] = now, ["targets"] = new JsonArray(new JsonObject { ["target"] = prepared.Target, ["runtimeRoot"] = prepared.RuntimeRoot, ["disposition"] = prepared.Disposition, ["newlyCreated"] = new JsonObject { ["root"] = prepared.NewlyCreated.Root, ["controlDirectory"] = prepared.NewlyCreated.ControlDirectory, ["marker"] = prepared.NewlyCreated.Marker }, ["controlDirectory"] = ".hvo-deploy", ["markerDigest"] = prepared.MarkerDigest, ["creatingRunId"] = prepared.CreatingRunId, ["status"] = "prepared" }) };
        WriteJson(StateRoot + "/prepare-ledger.json", ledger); WriteJson(StateRoot + "/prepare-manifest.json", ledger); WriteJson(EvidenceRoot + "/prepare.json", PartialPrepareRecovery.PrepareEvidence(ledger));
        Write(StateRoot + "/private-upload-registry.json", "[]\n");
    }
    public async Task<JsonObject> ImportAsync(string input)
    {
        var metadata = Parse(Read(input + "/case.json")); replay = true; LogicalRoot = metadata.Text("fixture");
        Inventory = Parse(Read(input + "/inventory.json")); WriteJson(InventoryPath, Inventory);
        foreach (var tree in new[] { "remote", "state", "evidence" }) CopyTree(input + "/" + tree, Directory + "/" + tree);
        var options = metadata["options"]!; RunId = options.Text("--run-id"); Mode = options.Text("--mode");
        var environment = metadata["env"]!;
        BadHost = environment["FAKE_PREPARE_IDENTITY_MISMATCH_HOST"]?.GetValue<string>();
        BadDaemon = environment["FAKE_DOCKER_MISMATCH"]?.GetValue<string>()?.Replace("-context", "", StringComparison.Ordinal);
        Resource = environment["FAKE_PARTIAL_PREPARE_RESOURCE"]?.GetValue<string>();
        ResourceAfter = int.Parse(environment["FAKE_PARTIAL_PREPARE_RESOURCE_AFTER"]?.GetValue<string>() ?? "1", System.Globalization.CultureInfo.InvariantCulture);
        var imageState = input + "/image-state";
        if (System.IO.Directory.Exists(imageState)) foreach (var file in System.IO.Directory.EnumerateFiles(imageState))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("container-", StringComparison.Ordinal)) DockerResources.Add("logic-context:container:" + name[10..]);
            if (name.StartsWith("network-", StringComparison.Ordinal)) { var split = name.IndexOf("-context-", StringComparison.Ordinal); if (split >= 0) DockerResources.Add(name[8..(split + 8)] + ":network:" + name[(split + 9)..]); }
        }
        try { Contract = await SplitHostInventory.ReadAsync(InventoryPath, Mode, new ProcessRunner(), CancellationToken.None); }
        catch (InstallerException) { /* Invalid-inventory vectors must reach the real phase validator too. */ }
        return metadata;
    }
    public static void CopyTree(string source, string destination)
    {
        if (!System.IO.Directory.Exists(source)) return;
        System.IO.Directory.CreateDirectory(destination, File.GetUnixFileMode(source));
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            var target = destination + "/" + entry.Name;
            if (entry.LinkTarget is { } link) File.CreateSymbolicLink(target, link);
            else if (entry is DirectoryInfo) CopyTree(entry.FullName, target);
            else { File.Copy(entry.FullName, target); File.SetUnixFileMode(target, File.GetUnixFileMode(entry.FullName)); }
        }
    }

    public void Legacy()
    {
        Inventory["schemaVersion"] = 7; Inventory.Remove("productRoot");
        foreach (var app in new[] { Inventory["logicHost"]! }.Concat(Inventory.Array("cameraAgents").Select(n => n!)))
            foreach (var field in LegacyTargetKeys.Concat(ReferenceEquals(app, Inventory["logicHost"]) ? ["friendlyName"] : Array.Empty<string>())) ((JsonObject)app).Remove(field);
        var catalog = Inventory.Array("catalogs")[0]!;
        Inventory["catalog"] = new JsonObject(LegacyCatalogKeys.Select(key => KeyValuePair.Create<string, JsonNode?>(key, catalog[key]!.DeepClone())));
        Inventory["deployment"]!["catalog"] = new JsonObject(LegacyInstallKeys.Select(key => KeyValuePair.Create<string, JsonNode?>(key, catalog[key]!.DeepClone()))); Inventory.Remove("catalogs");
    }
    public static void Write(string path, string text) { File.WriteAllText(path, text); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    public static void WriteJson(string path, JsonNode node) => Write(path, node.ToJsonString(Options));
    public PartialPrepareRequest Request(bool inspect = false) => new(InventoryPath, Mode, RunId, StateRoot, EvidenceRoot, inspect, inspect ? null : RunId);
    public Task<JsonObject> RunAsync(bool inspect = false, Action<string>? boundary = null, CancellationToken token = default) => new PartialPrepareRecovery(this, new ProcessRunner(), boundary).RunAsync(Request(inspect), token);
    public IDeploymentFiles Open(DeploymentTarget target, CancellationToken token) => new FakeFiles(this, target, token);
    public Task<ProcessResult> DockerAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var args = arguments.ToArray(); DockerCalls.Add(args);
        if (args[0] == "context")
        {
            var target = Contract.Targets.First(t => t.DockerContext == args[2]); return Result(0, new JsonObject { ["name"] = target.DockerContext, ["host"] = "ssh://" + target.SshHost }.ToJsonString());
        }
        Assert.AreEqual("--host", args[0]); var t = Contract.Targets.First(t => "ssh://" + t.SshHost == args[1]);
        if (args[2] == "info") return Result(0, new JsonObject { ["ID"] = BadDaemon == t.Name ? "changed-daemon" : t.DockerDaemonIdentity, ["Name"] = t.HostName, ["Architecture"] = t.Architecture, ["OSType"] = "linux", ["ServerVersion"] = "test" }.ToJsonString());
        if (args[3] == "inspect") return (DockerResources.Contains(t.DockerContext + ":" + args[2] + ":" + args[^1]) || args[^1] == (args[2] == "container" ? PresentContainer : PresentNetwork)) ? Result(0, "\"unexpected\"") : Result(1, "", "Error: No such " + args[2] + ": " + args[^1]);
        if (Resource == t.DockerContext + ":" + args[2] + ":" + args[^1]) { ResourceProbes++; if (ResourceProbes >= ResourceAfter) return Result(0, "unexpected-resource\n"); }
        return Result(0, "");
    }
    private static Task<ProcessResult> Result(int code, string output, string error = "") => Task.FromResult(new ProcessResult(code, output, error));
    private sealed class FakeFiles(DeploymentFakeHost host, DeploymentTarget target, CancellationToken token) : IDeploymentFiles
    {
        private readonly IDeploymentFiles files = host.UseShellPrimitives ? new SshDeploymentFiles("", token, local: true) : new DeploymentFakeFiles();
        public string Call(string operation, params string[] args)
        {
            token.ThrowIfCancellationRequested(); host.BeforePrimitive?.Invoke(operation, args);
            if (operation == "probe") return $"{(host.BadHost == target.Name ? "changed-machine" : target.HostIdentity)} {target.HostName} {NativeLinux.getuid()} {NativeLinux.getuid()} {target.Architecture}";
            var result = files.Call(operation, args.Select(host.Physical).ToArray()); host.AfterPrimitive?.Invoke(operation, args); return result;
        }
        public void Dispose() => files.Dispose();
    }
    public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
}
