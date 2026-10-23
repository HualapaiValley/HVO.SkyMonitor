using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment.Contracts;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment;

/// <summary>Registered private transport shared by the preflight ancillary contracts and later phase ports.</summary>
internal sealed class PreflightUploadTransport(IDeploymentHost host, SplitHostInventory inventory, PartialPrepareState state,
    string stateRoot, string phase, Action<string>? boundary = null)
{
    private JsonArray Registry() => JsonNode.Parse(PartialPrepareState.ReadPrivate(stateRoot + "/private-upload-registry.json")) as JsonArray ?? throw Error("private-upload-registry-invalid");
    public void Register(DeploymentTarget target, string path, string kind)
    {
        Require(phase is "up" or "bootstrap" or "smoke" or "measure" or "acceptance-run" or "transient-confirm" or "down", "private-upload-registry-invalid");
        Require(kind is "upload-temp" or "remote-private" && ExistingDownHost.AllowedRemote(path, target) && (kind == "upload-temp") == path.Contains("/.hvo-deploy/uploads/", StringComparison.Ordinal), "private-path-not-allowed");
        var entries = Registry(); var node = SplitHostPreflight.Target(inventory, target.Name);
        var found = entries.Where(n => n!.Text("path") == path).ToArray();
        Require(found.All(n => n!.Text("phase") == phase && n.Text("kind") == kind && JsonNode.DeepEquals(n!["target"], node)), "private-path-already-registered");
        if (found.Length == 0) entries.Add(new JsonObject { ["phase"] = phase, ["kind"] = kind, ["target"] = node.DeepClone(), ["path"] = path });
        boundary?.Invoke("private-registry-publication:" + Path.GetFileName(path)); state.WriteState("private-upload-registry.json", entries);
    }
    public void Forget(string path)
    {
        var entries = Registry();
        state.WriteState("private-upload-registry.json", new JsonArray(entries.Where(n => n!.Text("path") != path).Select(n => n!.DeepClone()).ToArray()));
    }
    public void Cleanup(DeploymentTarget target, string path, CancellationToken token)
    {
        Require(ExistingDownHost.AllowedRemote(path, target) && path.StartsWith(target.RuntimeRoot + "/.hvo-deploy/uploads/", StringComparison.Ordinal), "private-path-not-allowed");
        using var files = host.Open(target, token); RequireComponents(files, Path.GetDirectoryName(path)!);
        var node = DeploymentNode.Parse(files.Call("stat", path));
        Require(node is null || node is { File: true, Links: 1 }, "unsafe-private-file");
        if (node is not null) files.Call("unlink", path);
        Require(DeploymentNode.Parse(files.Call("stat", path)) is null, "private-file-retained");
    }
    public void Copy(string source, DeploymentTarget target, string destination, CancellationToken token)
    {
        Require(Root(destination) && destination.StartsWith(target.RuntimeRoot + "/", StringComparison.Ordinal), "private-path-not-allowed");
        Require(File.Exists(source) && new FileInfo(source).LinkTarget is null, "unsafe-private-file");
        using var input = SafeFileSystem.OpenRegularFileRead(source);
        var uploadRoot = target.RuntimeRoot + "/.hvo-deploy/uploads";
        var temporary = uploadRoot + "/hvo-upload-" + phase + "-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".tmp";
        using var files = host.Open(target, token);
        RequireComponents(files, target.RuntimeRoot);
        foreach (var directory in new[] { target.RuntimeRoot + "/.hvo-deploy", uploadRoot })
        {
            var node = DeploymentNode.Parse(files.Call("stat", directory)); Require(node is null || node.Directory, "unsafe-private-path");
            if (node is null) files.Call("mkdir", directory);
        }
        Register(target, temporary, "upload-temp");
        try
        {
            var descriptor = files.Call("create", temporary); var buffer = new byte[48 * 1024]; int count;
            while ((count = input.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); files.Call("append-bytes", descriptor, Convert.ToBase64String(buffer, 0, count)); }
            boundary?.Invoke("abrupt-after-private-scp-upload");
            RequireComponents(files, Path.GetDirectoryName(destination)!);
            var node = DeploymentNode.Parse(files.Call("stat", temporary)); Require(node is { File: true, Links: 1 }, "unsafe-private-file");
            files.Call("replace", temporary, destination);
        }
        catch (PartialPrepareCrashException) { throw; }
        catch { Cleanup(target, temporary, token); Forget(temporary); throw; }
        Cleanup(target, temporary, token); Forget(temporary);
    }
    private static void RequireComponents(IDeploymentFiles files, string path)
    {
        var current = "";
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) { current += "/" + part; Require(DeploymentNode.Parse(files.Call("stat", current)) is { Directory: true }, "unsafe-private-path"); }
    }
    public static async Task RequireResourceAbsentAsync(IDeploymentHost host, string context, string kind, string name, CancellationToken token)
    {
        Require(kind is "volume" or "network", "invalid-resource-kind");
        var result = await host.DockerAsync(["--context", context, kind, "inspect", name], token).ConfigureAwait(false);
        var diagnostic = (result.StandardOutput + result.StandardError).Trim();
        Require(result.ExitCode != 0 && (diagnostic == "Error: No such " + kind + ": " + name || diagnostic == "Error response from daemon: " + (kind == "volume" ? "get " + name + ": no such volume" : "network " + name + " not found")), "resource-absence-unverified");
    }
}
