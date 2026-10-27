using System.Globalization;
using System.Text.Json.Nodes;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

internal sealed class PreflightFixture : IDisposable
{
    public DeploymentFakeHost Host { get; } = new();
    public string Fault { get; set; } = "";
    public int Opens { get; private set; }
    private int factIndex;
    public List<string> Operations { get; } = [];
    public List<string> Status { get; } = [];
    public PreflightRequest Request => new(Host.InventoryPath, Host.Mode, Host.RunId, Host.StateRoot, Host.EvidenceRoot);
    public async Task StageAsync()
    {
        Host.Inventory["secretSource"]!["path"] = Host.Directory + "/secrets.env";
        await Host.StageAsync();
        Directory.Delete(Host.StateRoot, true); Directory.Delete(Host.EvidenceRoot, true);
        foreach (var target in Host.Contract.Targets) if (Directory.Exists(Host.Physical(target.RuntimeRoot))) Directory.Delete(Host.Physical(target.RuntimeRoot), true);
        DeploymentFakeHost.Write(Host.Directory + "/secrets.env", string.Concat(Host.Inventory["secretSource"]!.Array("requiredReferences").Select(n => n!.GetValue<string>() + "=fixture-secret-never-print\n")));
        Host.PrimitiveHandler = (op, args) =>
        {
            Operations.Add(op);
            if (op == "facts")
            {
                Opens++; if (Fault == "unreachable") throw Error("host-disconnected"); if (Fault == "missing-bash") throw Error("tool-unavailable");
                // Targets are opened in inventory order. This fixture's fact request has no remote mutation.
                var target = Host.Contract.Targets[factIndex++ % Host.Contract.Targets.Count];
                return string.Join('\t', Fault == "host-id" ? "other" : target.HostIdentity, Fault == "host-name" ? "other" : target.HostName,
                    NativeLinux.getuid().ToString(CultureInfo.InvariantCulture), Fault == "host-architecture" ? "other" : target.Architecture,
                    Fault == "facts" ? "bad" : "4", "8192000", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), "unsupported", "unsupported");
            }
            return op switch
            {
                "uid" => NativeLinux.getuid().ToString(CultureInfo.InvariantCulture),
                "space" => "1000000",
                "sockets" => Fault == "missing-ss" ? "unsupported" : Fault == "host-port" ? "present" : "none",
                "tcp" => Fault == "tcp" ? "unreachable" : Fault == "missing-timeout" ? "tool-unavailable" : "reachable",
                "http-reachable" => Fault == "http" ? "unreachable" : Fault == "missing-curl" ? "tool-unavailable" : "reachable",
                _ => null
            };
        };
        Host.DockerResponse = args =>
        {
            if (args[0] == "context") return new(Fault == "context-unavailable" ? 1 : 0, Fault == "context-mismatch" ? "other" : args[^1], "");
            var target = Host.Contract.Targets.First(t => t.DockerContext == args[1]);
            if (args[2] == "info")
            {
                var info = new JsonObject
                {
                    ["ID"] = Fault == "daemon-id" ? "other" : target.DockerDaemonIdentity,
                    ["Name"] = Fault == "daemon-name" ? "other" : target.HostName,
                    ["Architecture"] = Fault == "unsupported-architecture" ? "ppc64le" : Fault == "architecture" ? null : target.Architecture,
                    ["OSType"] = Fault == "os" ? null : Fault == "windows" ? "windows" : "linux",
                    ["ServerVersion"] = Fault == "version" ? null : "28.0.1"
                };
                return new(Fault == "daemon-unreachable" ? 1 : 0, Fault == "docker-json" ? "{" : info.ToJsonString(), "private diagnostic");
            }
            if (args[2] == "compose") return new(Fault == "compose-missing" ? 1 : 0, Fault == "compose-version" ? "invalid" : "v2.39.1", "");
            Assert.AreEqual("ps", args[2]); return new(Fault == "containers-query" ? 1 : 0, Fault == "docker-port" ? "container" : "", "");
        };
    }
    public Task<JsonObject> RunAsync(Action<string>? boundary = null, CancellationToken token = default)
    {
        factIndex = 0; return new SplitHostPreflight(Host, new ProcessRunner(), boundary, Status.Add).RunAsync(Request, token);
    }
    public JsonObject Manifest => Parse(File.ReadAllText(Host.StateRoot + "/manifest.json"));
    public JsonObject Evidence => Parse(File.ReadAllText(Host.EvidenceRoot + "/preflight.json"));
    public void Dispose() => Host.Dispose();
}
