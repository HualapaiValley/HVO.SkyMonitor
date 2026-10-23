using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment.Contracts;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment;

internal sealed record SplitHostInventory(JsonObject Document, string Sha256, string Mode)
{
    public int Schema => Document["schemaVersion"]!.GetValue<int>();
    public string Revision => Document["source"]!.Text("revision");
    public string Installation => Document.Text("installationId");
    public IReadOnlyList<DeploymentTarget> Targets => new[] { Document["logicHost"]! }
        .Concat(Document.Array("cameraAgents").Select(node => node!))
        .Concat(Document["sharedServices"] is { } shared ? [shared] : [])
        .Select(node => new DeploymentTarget(node.Text("name"), node.Text("sshHost"), node.Text("dockerContext"),
            node.Text("expectedArchitecture"), node.Text("expectedHostName"), node.Text("expectedHostIdentity"),
            node.Text("expectedDockerDaemonIdentity"), node.Text("runtimeRoot"), node.Text("runtimeOwner"),
            ReferenceEquals(node, Document["sharedServices"]) ? null : ReferenceEquals(node, Document["logicHost"]) ? "logicHost" : "cameraAgent",
            node["instanceId"]?.GetValue<string>())).ToArray();
    public string Project(DeploymentTarget target) => Document["deployment"]!["resources"]!.Text("project") + "-" +
        (Schema == 7 ? target.Component == "logicHost" ? "logic" : target.Name : target.InstanceId!.Replace("-", "", StringComparison.Ordinal));
    public string Marker(DeploymentTarget target, string run) => Hash(Mode == "isolated"
        ? $"v1\nmode={Mode}\nrun={run}\ninventory={Sha256}\ntarget={target.Name}\nroot={target.RuntimeRoot}\nowner={target.RuntimeOwner}\n"
        : $"v1\nmode={Mode}\ninstallation={Installation}\ntarget={target.Name}\nroot={target.RuntimeRoot}\nowner={target.RuntimeOwner}\n");
    public static string LockName(DeploymentTarget target) => ".hvo-deploy-prepare-" + Hash($"v1\ntarget={target.Name}\nroot={target.RuntimeRoot}\n")[..32] + ".lock";

    public static async Task<SplitHostInventory> ReadAsync(string path, string mode, IProcessRunner runner, CancellationToken token)
    {
        Require(mode is "isolated" or "persistent", "invalid-mode");
        Require(new FileInfo(path).LinkTarget is null && File.Exists(path), "inventory-missing-or-symlink");
        var bytes = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
        Require(bytes.Length <= 4 * 1024 * 1024, "inventory-too-large");
        var original = Parse(bytes);
        var schema = original["schemaVersion"]!.GetValue<int>();
        Require(schema is 7 or 8, "schema-or-value-invalid");
        var filter = Resource($"InventoryV{schema}.jq");
        var result = await runner.RunAsync("jq", ["-e", "--arg", "mode", mode, filter, path], token).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Trim() == "true", "schema-or-value-invalid");
        var projected = (JsonObject)original.DeepClone();
        if (schema == 7)
        {
            ValidateLegacyCollisions(original);
            projected["schemaVersion"] = 8;
            projected["productRoot"] = "/hvo-schema7-recovery-validation";
            var catalog = original["catalog"]!;
            var old = original["deployment"]!["catalog"]!;
            projected["catalogs"] = new JsonArray(new JsonObject
            {
                ["catalogId"] = "legacy", ["displayName"] = "Legacy retained catalog", ["kind"] = catalog["kind"]!.DeepClone(),
                ["version"] = catalog["version"]!.DeepClone(), ["schemaVersion"] = "legacy", ["preprocessingVersion"] = "legacy",
                ["sha256"] = catalog["sha256"]!.DeepClone(), ["length"] = catalog["length"]!.DeepClone(), ["rowCount"] = catalog["rowCount"]!.DeepClone(),
                ["bundlePath"] = old["bundlePath"]!.DeepClone(), ["installRoot"] = "/hvo-schema7-recovery-validation/catalog", ["allowFixture"] = old["allowFixture"]!.DeepClone()
            });
            projected.Remove("catalog"); ((JsonObject)projected["deployment"]!).Remove("catalog");
            var apps = new[] { projected["logicHost"]! }.Concat(projected.Array("cameraAgents").Select(n => n!)).ToArray();
            for (var i = 0; i < apps.Length; i++)
            {
                var app = apps[i]; var id = i == 0 ? "00000000-0000-4000-8000-000000000001" : "00000000-0000-4000-8000-000000000002";
                app["friendlyName"] ??= app["name"]!.DeepClone(); app["instanceId"] = id; app["applicationIdentity"] = app["name"]!.DeepClone();
                app["catalogId"] = "legacy"; app["cookieName"] = "hvo.skymonitor." + id.Replace("-", "", StringComparison.Ordinal);
                foreach (var field in new[] { "sshHost", "dockerContext", "expectedHostName", "expectedHostIdentity", "expectedDockerDaemonIdentity" })
                    app[field] = i == 0 ? "schema7-logic" : "schema7-camera-" + (i - 1);
            }
            if (projected["sharedServices"] is { } shared)
                foreach (var field in new[] { "sshHost", "dockerContext", "expectedHostName", "expectedHostIdentity", "expectedDockerDaemonIdentity" }) shared[field] = "schema7-shared";
            // Validate the projection in memory through jq without writing a recovery file to an ambient TMPDIR.
            result = await runner.RunAsync("jq", ["-ne", "--arg", "mode", mode, "--argjson", "inventory", Canonical(projected), "$inventory | (" + Resource("InventoryV8.jq") + ")"], token).ConfigureAwait(false);
            Require(result.ExitCode == 0 && result.StandardOutput.Trim() == "true", "schema-or-value-invalid");
        }
        ValidateCrossFields(projected, mode, schema == 8);
        // jq's canonical numeric/string representation is the retained shell's immutable hash contract.
        result = await runner.RunAsync("jq", ["-S", "-c", ".", path], token).ConfigureAwait(false);
        Require(result.ExitCode == 0 && DeepEqual(Parse(result.StandardOutput), original), "inventory-changed");
        return new SplitHostInventory(original, Hash(result.StandardOutput), mode);
    }
    internal static string Resource(string name)
    {
        using var stream = typeof(SplitHostInventory).Assembly.GetManifestResourceStream("HVO.SkyMonitor.Deployment.SplitHost." + name) ?? throw Error("missing-inventory-contract");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    private static bool DeepEqual(JsonNode first, JsonNode second) => JsonNode.DeepEquals(first, second);
    private static void ValidateLegacyCollisions(JsonObject doc)
    {
        var targets = new[] { doc["logicHost"]! }.Concat(doc.Array("cameraAgents").Select(n => n!)).Concat(doc["sharedServices"] is { } shared ? [shared] : []).ToArray();
        foreach (var key in new[] { "sshHost", "dockerContext", "expectedHostName", "expectedHostIdentity", "expectedDockerDaemonIdentity" })
            foreach (var group in targets.GroupBy(t => t.Text(key), StringComparer.Ordinal).Where(g => g.Count() > 1))
                Require(key is not ("sshHost" or "dockerContext") && group.Count() == 2 && group.Contains(doc["logicHost"]!) && doc["sharedServices"] is { } infra && group.Contains(infra), "target-identity-collision");
    }
    private static void ValidateCrossFields(JsonObject doc, string mode, bool layout)
    {
        var inventory = new SplitHostInventory(doc, "", mode); var targets = inventory.Targets;
        var apps = targets.Where(t => t.Component is not null).ToArray();
        Require(targets.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() == targets.Count, "duplicate-target-name");
        foreach (var a in targets)
        foreach (var b in targets.Where(b => b.Name != a.Name))
        {
            Require(a.SshHost != b.SshHost || (a.HostName, a.HostIdentity, a.Architecture) == (b.HostName, b.HostIdentity, b.Architecture), "target-identity-collision");
            Require(a.DockerContext != b.DockerContext || (a.DockerDaemonIdentity, a.Architecture) == (b.DockerDaemonIdentity, b.Architecture), "target-identity-collision");
            Require(a.HostIdentity != b.HostIdentity || (a.HostName, a.Architecture) == (b.HostName, b.Architecture), "target-identity-collision");
            Require(a.DockerDaemonIdentity != b.DockerDaemonIdentity || a.Architecture == b.Architecture, "target-identity-collision");
            Require((a.HostIdentity, a.RuntimeRoot) != (b.HostIdentity, b.RuntimeRoot), "target-resource-collision");
        }
        var appNodes = new[] { doc["logicHost"]! }.Concat(doc.Array("cameraAgents").Select(n => n!)).ToArray();
        var ports = appNodes.SelectMany(t => t.Array("ports").Select(p => (t.Text("expectedHostIdentity"), p!.GetValue<int>()))).ToArray();
        Require(ports.Distinct().Count() == ports.Length, "target-resource-collision");
        foreach (var endpoint in doc.Array("serviceEndpoints"))
            Require(endpoint!.Array("fromTargets").All(n => targets.Any(t => t.Name == n!.GetValue<string>())), "unknown-endpoint-route");
        Require(doc["images"]!.Text("distributionMode") != "registry" || doc["images"]!["builder"]!.Text("driver") != "docker", "cameraagent-registry-requires-docker-container");
        var required = doc["secretSource"]!.Array("requiredReferences").Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var refs = doc["deployment"]!.Array("secretMappings").Select(n => n!.Text("reference")).Concat(doc.Array("cameraAgents").Select(n => n!.Text("ownerPasswordSecretReference"))).ToList();
        foreach (var section in new[] { doc["deployment"]!["services"]!["sql"], doc["deployment"]!["services"]!["redis"], doc["deployment"]!["services"]!["smtp"], doc["deployment"]!["certificates"], doc["deployment"]!["automation"], doc["deployment"]!["deviceBootstrap"] })
            refs.AddRange(((JsonObject)section!).Where(p => p.Key.EndsWith("Reference", StringComparison.Ordinal) && p.Value is not null).Select(p => p.Value!.GetValue<string>()));
        Require(refs.All(required.Contains), "unknown-deployment-secret-reference");
        if (mode == "persistent")
        {
            Require(doc.Array("catalogs").All(n => n!.Text("kind") == "production" && !n.Flag("allowFixture")) &&
                doc["deployment"]!["services"]!.Text("mode") == "existing" && doc["deployment"]!["services"]!["smtp"]!.Text("kind") == "production" &&
                appNodes.All(n => n.Text("publicEndpoint").StartsWith("https://", StringComparison.Ordinal) && n.Array("trustedProxyAddresses").Count > 0), "persistent-mode-requires-production-https");
        }
        if (!layout) return;
        var root = doc.Text("productRoot"); Require(Root(root), "noncanonical-product-root");
        Require(mode != "persistent" || root == "/var/lib/hvo-sky" || root.StartsWith("/tmp/hvo-deploy-test.", StringComparison.Ordinal), "persistent-product-root-mismatch");
        foreach (var target in apps)
        {
            Require(target.RuntimeRoot == root + (target.Component == "logicHost" ? "/logichosts/" : "/cameraagents/") + target.InstanceId, "instance-root-mismatch");
            var node = appNodes.Single(n => n.Text("name") == target.Name);
            Require(node.Text("cookieName") == "hvo.skymonitor." + target.InstanceId!.Replace("-", "", StringComparison.Ordinal), "identity-or-path-collision");
            Require(doc.Array("catalogs").Any(c => c!.Text("catalogId") == node.Text("catalogId")), "unknown-catalog-reference");
        }
        foreach (var field in new[] { "instanceId", "applicationIdentity", "cookieName" }) Require(appNodes.Select(n => n.Text(field)).Distinct(StringComparer.Ordinal).Count() == apps.Length, "identity-or-path-collision");
        foreach (var field in new[] { "catalogId", "installRoot" }) Require(doc.Array("catalogs").Select(n => n!.Text(field)).Distinct(StringComparer.Ordinal).Count() == doc.Array("catalogs").Count, "identity-or-path-collision");
        foreach (var catalog in doc.Array("catalogs")) Require(catalog!.Text("installRoot") == root + "/catalogs/" + catalog.Text("catalogId"), "catalog-root-mismatch");
    }
}
