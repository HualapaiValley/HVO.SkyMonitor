using System.Text.Json.Nodes;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment;

internal static class PreflightPrivateUploads
{
    public static void Initialize(PartialPrepareState state, SplitHostInventory inventory, string stateRoot)
    {
        if (!state.Exists("private-upload-registry.json")) { state.WriteState("private-upload-registry.json", new JsonArray()); return; }
        var entries = JsonNode.Parse(PartialPrepareState.ReadPrivate(stateRoot + "/private-upload-registry.json")) as JsonArray;
        Require(entries is { Count: <= 256 }, "private-upload-registry-invalid");
        foreach (var entry in entries)
        {
            Keys(entry!, "phase kind target path");
            Require(entry!.Text("phase") is "up" or "bootstrap" or "smoke" or "measure" or "acceptance-run" or "transient-confirm" or "down", "private-upload-registry-invalid");
            var target = inventory.Targets.SingleOrDefault(t => t.Name == entry!["target"]!.Text("name"));
            Require(target is not null && JsonNode.DeepEquals(entry!["target"], SplitHostPreflight.Target(inventory, target.Name)), "private-upload-registry-invalid");
            var path = entry.Text("path"); var kind = entry.Text("kind");
            if (kind == "local-private")
                Require(Root(path) && path.StartsWith(stateRoot + "/", StringComparison.Ordinal) &&
                    (Path.GetFileName(path) == "central.headers" ? target.Component == "logicHost" : Match(Path.GetFileName(path), target.Name + "-(?:owner-password|antiforgery[.]headers|antiforgery[.]json|envelope[.]json|bootstrap-request[.]json)")), "private-upload-registry-invalid");
            else Require(kind is "remote-private" or "upload-temp" && ExistingDownHost.AllowedRemote(path, target) && (kind == "upload-temp") == path.Contains("/.hvo-deploy/uploads/", StringComparison.Ordinal), "private-upload-registry-invalid");
        }
    }
}
