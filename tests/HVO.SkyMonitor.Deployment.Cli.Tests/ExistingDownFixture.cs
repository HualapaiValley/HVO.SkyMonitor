using System.Globalization;
using System.Text.Json.Nodes;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

internal sealed class ExistingDownFixture : IDisposable
{
    public DeploymentFakeHost Host { get; } = new();
    public string Fault { get; set; } = "";
    public string Mode { get; set; } = "Off";
    public List<string> Queries { get; } = [];
    public List<string> ControlKeys { get; } = [];
    public int Stops { get; private set; }
    public int EastReads { get; private set; }
    private readonly Dictionary<string, int> reads = new(StringComparer.Ordinal);
    private readonly HashSet<string> stopped = new(StringComparer.Ordinal);
    public ExistingDownRequest Request => new(Host.InventoryPath, Host.Mode, Host.RunId, Host.StateRoot, Host.EvidenceRoot, "preserve", null);
    public async Task StageAsync()
    {
        Host.Inventory["deployment"]!["transient"]!["mode"] = Mode;
        Host.Inventory["secretSource"]!["path"] = Host.Directory + "/secrets.env";
        await Host.StageAsync();
        var prepare = Parse(DeploymentFakeHost.Read(Host.StateRoot + "/prepare-ledger.json"));
        var template = prepare.Array("targets")[0]!.DeepClone(); prepare.Array("targets").Clear();
        foreach (var target in Host.Contract.Targets)
        {
            var root = Host.Physical(target.RuntimeRoot); Directory.CreateDirectory(root + "/.hvo-deploy", SafeFileSystem.OwnerDirectoryMode); Directory.CreateDirectory(root + "/config/secrets", SafeFileSystem.OwnerDirectoryMode); Directory.CreateDirectory(root + "/config/private", SafeFileSystem.OwnerDirectoryMode); File.SetUnixFileMode(root, SafeFileSystem.OwnerDirectoryMode);
            DeploymentFakeHost.Write(root + "/unrelated-data", "keep-me");
            var entry = template.DeepClone(); entry["target"] = target.Name; entry["runtimeRoot"] = target.RuntimeRoot; entry["markerDigest"] = Host.Contract.Marker(target, Host.RunId); prepare.Array("targets").Add(entry);
        }
        prepare["phaseStatus"] = "passed"; prepare["completedAt"] = Now();
        DeploymentFakeHost.WriteJson(Host.StateRoot + "/prepare-ledger.json", prepare); DeploymentFakeHost.WriteJson(Host.StateRoot + "/prepare-manifest.json", prepare); DeploymentFakeHost.WriteJson(Host.EvidenceRoot + "/prepare.json", PartialPrepareRecovery.PrepareEvidence(prepare));
        DeploymentFakeHost.WriteJson(Host.StateRoot + "/up-manifest.json", prepare);
        Directory.CreateDirectory(Host.StateRoot + "/up-rendered", SafeFileSystem.OwnerDirectoryMode);
        foreach (var target in Host.Contract.Targets.Where(t => t.Component is not null)) DeploymentFakeHost.Write(Host.StateRoot + "/up-rendered/" + target.Name + ".env", "PINNED=1");
        DeploymentFakeHost.Write(Host.Directory + "/secrets.env", string.Concat(Host.Inventory["secretSource"]!.Array("requiredReferences").Select(n => n!.GetValue<string>() + "=fixture-secret-never-print\n")));
        Host.DockerResponse = args =>
        {
            if (args.Count > 2 && args[2] == "compose")
            {
                var project = args[4];
                if (args.Contains("stop")) { if (Fault == "compose") return new ProcessResult(1, "", "private diagnostic"); Stops++; stopped.Add(project); return new(0, "", ""); }
                return new(0, Fault == "recreated" || !stopped.Contains(project) ? new string('a', 64) : "", "");
            }
            if (args.Count > 3 && args[2] == "container" && args[3] == "inspect") return new(0, new JsonObject { ["project"] = Host.Contract.Project(Host.Contract.Targets.First(t => "ssh://" + t.SshHost == args[1])), ["service"] = "cameraagent", ["running"] = true }.ToJsonString(), "");
            return null;
        };
        Host.PrimitiveHandler = (operation, args) =>
        {
            if (operation != "http") return null;
            var output = args[Array.IndexOf(args, "--output") + 1]; var cookies = args[Array.IndexOf(args, "--cookie-jar") + 1]; var url = args[^1]; Queries.Add(url);
            var target = Host.Contract.Targets.First(t => Beneath(output, t.RuntimeRoot)); var device = "device-" + target.Name;
            var final = url.Contains("-final-", StringComparison.Ordinal); var status = 200; string content;
            if (url.EndsWith("/Account/Login", StringComparison.Ordinal))
            {
                if (Fault == "authentication" && args.Contains("--data-urlencode", StringComparer.Ordinal)) status = 401;
                content = "<input name=\"__RequestVerificationToken\" value=\"fixture\">";
            }
            else if (url.EndsWith("/antiforgery", StringComparison.Ordinal)) content = "{\"headerName\":\"X-CSRF\",\"requestToken\":\"fixture-token\"}";
            else if (url.EndsWith("/pause", StringComparison.Ordinal))
            {
                var headerPath = args.Select((value, index) => (value, index)).Single(pair => pair.value == "--header" && args[pair.index + 1].StartsWith('@')).index;
                var headerFile = args[headerPath + 1][1..];
                var lines = File.ReadAllLines(Host.Physical(headerFile));
                Assert.AreEqual(1, lines.Count(line => line.StartsWith("Idempotency-Key:", StringComparison.Ordinal)));
                Assert.AreEqual(1, lines.Count(line => line.StartsWith("X-CSRF:", StringComparison.Ordinal)));
                var controlKey = lines.Single(line => line.StartsWith("Idempotency-Key:", StringComparison.Ordinal));
                Assert.IsTrue(headerFile.EndsWith("-control.headers", StringComparison.Ordinal)); Assert.IsFalse(ControlKeys.Contains(controlKey, StringComparer.Ordinal)); ControlKeys.Add(controlKey);
                var isFinal = output.Contains("final-pause", StringComparison.Ordinal); status = Fault == "pause-conflict" ? 409 : 200;
                content = new JsonObject { ["state"] = 3, ["version"] = isFinal && Fault == "final-version" ? 11 : 10, ["changed"] = true, ["replayed"] = false, ["requestedUtc"] = "2026-08-07T00:00:00Z", ["completedUtc"] = Fault == "pause-receipt" ? "2026-08-06T00:00:00Z" : "2026-08-07T00:00:01Z" }.ToJsonString();
            }
            else if (url.Contains("/continuity?", StringComparison.Ordinal))
            {
                var continuity = Continuity(device);
                if (Fault == "mismatch") continuity["configuredAgentId"] = "other";
                if (Fault == "duplicate") continuity["durable"]!.Array("captureSequences").Add(continuity["durable"]!.Array("captureSequences")[0]!.DeepClone());
                if (Fault == "change" && !url.EndsWith("boundary", StringComparison.Ordinal)) continuity["durable"]!["captureSequences"]![0]!["lastSequence"] = 3;
                if (Fault == "hash-drift" && !url.EndsWith("boundary", StringComparison.Ordinal) || Fault == "final-hash" && final) continuity["activeConfigurationSha256"] = new string('b', 64);
                content = continuity.ToJsonString();
            }
            else
            {
                var count = reads.GetValueOrDefault(target.Name) + 1; reads[target.Name] = count; if (target.Name == "east") EastReads = count;
                var summary = Summary(device, Mode, Fault == "delay-west" ? "changing-then-stable" : Fault, count, final);
                if (Fault == "delay-west" && target.Name == "west" && count == 1) summary["rawIngress"]!["value"]!["leasedCount"] = 1;
                content = summary.ToJsonString();
            }
            if (Fault == "pascal" && content.StartsWith('{'))
            {
                JsonNode? Pascal(JsonNode? value) => value switch
                {
                    JsonObject obj => new JsonObject(obj.Select(pair => KeyValuePair.Create(new[] { "deviceId", "configuredAgentId", "isProvisioned", "activeConfigurationSha256", "state", "version", "replayed", "captureControl", "value" }.Contains(pair.Key, StringComparer.Ordinal) ? char.ToUpperInvariant(pair.Key[0]) + pair.Key[1..] : pair.Key, Pascal(pair.Value)))),
                    JsonArray array => new JsonArray(array.Select(Pascal).ToArray()), _ => value?.DeepClone()
                };
                content = Pascal(Parse(content))!.ToJsonString();
            }
            DeploymentFakeHost.Write(Host.Physical(output), content); DeploymentFakeHost.Write(Host.Physical(cookies), "fixture-cookie"); return status.ToString(CultureInfo.InvariantCulture);
        };
    }
    public static JsonObject Continuity(string device = "device-east") => new() { ["deviceId"] = device, ["configuredAgentId"] = device, ["isProvisioned"] = true, ["activeConfigurationSha256"] = new string('a', 64), ["durable"] = new JsonObject { ["rawIngressDatabaseExists"] = true, ["captureSequences"] = new JsonArray(new JsonObject { ["agentId"] = device, ["lastSequence"] = 2 }) } };
    public static JsonObject Summary(string device, string mode, string fault = "", int count = 1, bool final = false)
    {
        var captured = "2026-08-07T00:10:" + count.ToString("D2", CultureInfo.InvariantCulture) + "Z";
        if (fault == "equal-captured") captured = "2026-08-07T00:10:01Z";
        if (fault == "decreasing-captured") captured = count == 1 ? "2026-08-07T00:10:02Z" : "2026-08-07T00:10:01Z";
        if (fault == "pre-pause-captured") captured = "2026-08-06T23:59:59Z";
        if (final && fault == "final-time") captured = "2026-08-07T00:10:02Z";
        JsonObject Section(JsonObject value) => new() { ["freshness"] = "fresh", ["observedUtc"] = captured, ["value"] = value };
        JsonObject Queue(string availability) => new() { ["availability"] = availability, ["pendingCount"] = 0, ["leasedCount"] = 0, ["retryCount"] = 0, ["quarantineCount"] = 0, ["terminalCount"] = 0 };
        JsonObject Lane(string name, bool hybrid) => new() { ["name"] = name, ["required"] = true, ["pendingCount"] = hybrid ? 2 : 0, ["leasedCount"] = 0, ["retryCount"] = 0, ["quarantineCount"] = 0, ["pressureLevel"] = 0, ["pendingCaptures"] = hybrid ? new JsonArray(new JsonObject { ["agentId"] = device, ["captureSequence"] = 1 }, new JsonObject { ["agentId"] = device, ["captureSequence"] = 2 }) : new JsonArray() };
        var children = new JsonArray(Lane("standard", false)); if (mode == "Hybrid") children.Add(Lane("transient", true));
        var summary = new JsonObject
        {
            ["capturedUtc"] = captured,
            ["configuration"] = Section(new JsonObject { ["isCurrent"] = true, ["validationStatus"] = "validated", ["agentId"] = device, ["moduleType"] = "VirtualSky", ["transientDetection"] = mode, ["centralIntegration"] = "Enabled" }),
            ["captureControl"] = Section(new JsonObject { ["state"] = "Paused", ["version"] = 10, ["isInitialized"] = true }),
            ["rawIngress"] = Section(Queue("Accepting")),
            ["captureProcessing"] = Section(Queue("Healthy")),
            ["artifactOutbox"] = Section(Queue("Healthy")),
            ["captureLanes"] = Section(new JsonObject { ["availability"] = "Healthy", ["pendingCount"] = mode == "Hybrid" ? 2 : 0, ["leasedCount"] = 0, ["retryCount"] = 0, ["quarantineCount"] = 0, ["lanes"] = children }),
            ["transientWorker"] = Section(new JsonObject { ["availability"] = mode == "Hybrid" ? "Healthy" : "Disabled", ["pendingFrames"] = 0, ["pendingCandidates"] = 0, ["maximumCandidates"] = 32 })
        };
        var raw = summary["rawIngress"]!["value"]!; var lanes = summary["captureLanes"]!["value"]!; var worker = summary["transientWorker"]!["value"]!; var config = summary["configuration"]!["value"]!;
        raw["pendingCount"] = mode == "Hybrid" ? 2 : 0;
        var transient = mode == "Hybrid" ? children[1]! : null;
        switch (fault)
        {
            case "wrong": transient!.Array("pendingCaptures")[0]!["agentId"] = "other"; break;
            case "stale": transient!.Array("pendingCaptures")[0]!["captureSequence"] = 0; break;
            case "third": transient!.Array("pendingCaptures").Add(new JsonObject { ["agentId"] = device, ["captureSequence"] = 3 }); break;
            case "excess": transient!["pendingCount"] = 67; lanes["pendingCount"] = 67; break;
            case "fractional": transient!["pendingCount"] = 2.5; lanes["pendingCount"] = 2.5; break;
            case "excess-raw": raw["pendingCount"] = 7; break;
            case "fractional-raw": raw["pendingCount"] = 1.5; break;
            case "retry": transient!["retryCount"] = 1; lanes["retryCount"] = 1; break;
            case "quarantine": transient!["quarantineCount"] = 1; lanes["quarantineCount"] = 1; break;
            case "pressure": transient!["pressureLevel"] = 2; break;
            case "unrelated": children[0]!["pendingCount"] = 1; lanes["pendingCount"] = 3; break;
            case "busy": worker["pendingFrames"] = 1; break;
            case "unhealthy": worker["availability"] = "Unavailable"; break;
            case "stale-section": summary["captureLanes"]!["freshness"] = "stale"; break;
            case "stale-hybrid-worker": summary["transientWorker"]!["freshness"] = "stale"; break;
            case "config-mismatch": config["agentId"] = "other"; break;
            case "config-not-current": config["isCurrent"] = false; break;
            case "config-mode-missing": ((JsonObject)config).Remove("transientDetection"); break;
            case "malformed": lanes["lanes"] = new JsonObject(); break;
            case "terminal": summary["captureProcessing"]!["value"]!["terminalCount"] = 1; break;
            case "mode": config["transientDetection"] = "Hybrid"; break;
            case "pre-pause": summary["rawIngress"]!["observedUtc"] = "2026-08-06T23:59:59Z"; break;
            case "leased": raw["leasedCount"] = 1; break;
            case "safe-unsafe": if (count > 1) raw["leasedCount"] = 1; break;
            case "changing": config["centralIntegration"] = count % 2 == 0 ? "A" : "B"; break;
            case "changing-then-stable": config["centralIntegration"] = count > 1 ? "B" : "A"; break;
            case "final-unsafe": if (final) raw["leasedCount"] = 1; break;
            case "final-fingerprint": if (final) config["centralIntegration"] = "changed"; break;
            case "stale-async":
                summary["captureProcessing"]!["observedUtc"] = "2026-08-06T23:59:59Z"; summary["artifactOutbox"]!["observedUtc"] = "2026-08-06T23:59:59Z";
                summary["captureControl"]!["freshness"] = "stale"; summary["captureControl"]!["observedUtc"] = "2026-08-01T00:00:00Z"; summary["transientWorker"]!["freshness"] = "stale"; summary["transientWorker"]!["observedUtc"] = "2026-08-01T00:00:00Z"; break;
        }
        return summary;
    }
    public Task<JsonObject> RunAsync(Action<string>? boundary = null, CancellationToken token = default) => new ExistingDown(Host, new ProcessRunner(), "/source", boundary, attempts: Fault is "changing-then-stable" or "delay-west" ? 4 : 3, poll: TimeSpan.Zero).RunAsync(Request, token);
    public void Dispose() => Host.Dispose();
}
