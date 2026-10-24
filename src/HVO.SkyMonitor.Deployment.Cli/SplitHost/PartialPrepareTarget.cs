using System.Text;
using HVO.SkyMonitor.Deployment.Contracts;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment;

internal sealed class PartialPrepareTarget(IDeploymentFiles files, DeploymentTarget target)
{
    private static readonly string[] CreationKeys = ["rootNew", "controlNew", "markerNew"];
    private uint owner;
    private sealed record Bound(string Path, string Handle, DeploymentNode Node, string? Content);
    private DeploymentNode? Stat(string path) => DeploymentNode.Parse(files.Call("stat", path));
    private DeploymentNode Fstat(string handle) => DeploymentNode.Parse(files.Call("fstat", handle)) ?? throw Error("provenance-or-content-invalid");
    private string Read(string handle) => Encoding.UTF8.GetString(Convert.FromBase64String(files.Call("read", handle)));
    private string[] List(string path)
    {
        var content = Convert.FromBase64String(files.Call("list", path)); Require(content.Length <= 1024 * 1024, "directory-too-large");
        return Encoding.UTF8.GetString(content).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
    private string DescriptorPath(string handle) => files.Call("path", handle);
    public void Authenticate()
    {
        var response = files.Call("probe", target.RuntimeOwner).Split(' ');
        Require(response.Length == 5 && response[0] == target.HostIdentity && response[1] == target.HostName && response[2] == response[3] &&
            Architecture(response[4]) == target.Architecture, "provenance-or-content-invalid");
        owner = uint.Parse(response[2], System.Globalization.CultureInfo.InvariantCulture);
        Require(Root(target.RuntimeRoot), "unsafe-runtime-root");
    }
    internal static string Architecture(string value) => value switch { "amd64" or "x86_64" => "amd64", "arm64" or "aarch64" => "arm64", _ => "unsupported" };
    private Bound Bind(string path, bool directory, string? content = null, bool allowParent = false)
    {
        var node = Stat(path) ?? throw Error("provenance-or-content-invalid");
        Require((directory ? node.Directory : node.File && node.Links == 1) && node.Owner == owner &&
            (allowParent ? (node.Mode & 0x92) == 0x80 : node.Mode == (directory ? 448 : 384)), "provenance-or-content-invalid");
        var handle = files.Call("open", path);
        Require(Fstat(handle) == node && Stat(path) == node, "provenance-or-content-invalid");
        if (content is not null) Require(Read(handle) == content, "provenance-or-content-invalid");
        return new Bound(path, handle, node, content);
    }
    private void Revalidate(Bound bound)
    {
        Require(Stat(bound.Path) == bound.Node && Fstat(bound.Handle) == bound.Node, "provenance-or-content-invalid");
        if (bound.Content is not null) Require(Read(bound.Handle) == bound.Content, "provenance-or-content-invalid");
    }
    private Bound? Resolve(string path, string quarantine, bool absentAllowed, bool directory, string? content = null)
    {
        var original = Stat(path); var parked = Stat(quarantine);
        Require(original is null || parked is null, "provenance-or-content-invalid");
        Require(parked is null || absentAllowed, "provenance-or-content-invalid");
        var actual = original is not null ? path : parked is not null ? quarantine : null;
        Require(actual is not null || absentAllowed, "provenance-or-content-invalid");
        return actual is null ? null : Bind(actual, directory, content);
    }
    private void Components(string path, bool missingAllowed)
    {
        var current = "";
        foreach (var component in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + component;
            var node = Stat(current);
            if (node is null && missingAllowed) return;
            Require(node is { Directory: true }, "unsafe-path");
        }
    }
    public void Unprepared(string deploymentState)
    {
        Authenticate(); var root = target.RuntimeRoot; var parent = Path.GetDirectoryName(root)!;
        Components(parent, true);
        Require(Stat(parent + "/" + SplitHostInventory.LockName(target)) is null && Stat(parent + "/" + SplitHostInventory.LockName(target) + ".state") is null &&
            (deploymentState == "ready" || deploymentState == "needs_prepare" && Stat(root) is null), "unjournaled-prepare-artifacts");
    }
    public void Preserved(PreparedTarget prepared)
    {
        try
        {
            Authenticate(); Components(target.RuntimeRoot + "/.hvo-deploy", false);
            _ = Bind(target.RuntimeRoot, true); _ = Bind(target.RuntimeRoot + "/.hvo-deploy", true);
            _ = Bind(target.RuntimeRoot + "/.hvo-deploy/ownership", false, Marker(prepared.MarkerDigest));
            var lockPath = Path.GetDirectoryName(target.RuntimeRoot) + "/" + SplitHostInventory.LockName(target);
            var held = Bind(lockPath, false, Lock(prepared.MarkerDigest)); files.Call("lock", held.Handle);
            var state = Bind(lockPath + ".state", false);
            var content = Read(state.Handle); var lines = content.Split('\n');
            Require(lines.Length == 7 && lines[^1].Length == 0 && lines[0] == "HVO-DEPLOY-PREPARE-STATE\t1" && lines[1] == "marker\t" + prepared.MarkerDigest &&
                lines[2] == "creatingRun\t" + prepared.CreatingRunId && lines.Skip(3).Take(3).Select((line, i) => line == CreationKeys[i] + "\ttrue" || line == CreationKeys[i] + "\tfalse").All(b => b), "preserved-provenance-invalid");
            Revalidate(held); Revalidate(state);
        }
        catch (InstallerException) { throw Error("preserved-provenance-invalid"); }
    }
    public static string Marker(string digest) => $"HVO-DEPLOY-ROOT\t1\nmarker\t{digest}\n";
    public static string Lock(string digest) => $"HVO-DEPLOY-PREPARE-LOCK\t1\nmarker\t{digest}\n";
    public static string State(PreparedTarget prepared) => $"HVO-DEPLOY-PREPARE-STATE\t1\nmarker\t{prepared.MarkerDigest}\ncreatingRun\t{prepared.CreatingRunId}\nrootNew\t{Bool(prepared.NewlyCreated.Root)}\ncontrolNew\t{Bool(prepared.NewlyCreated.ControlDirectory)}\nmarkerNew\t{Bool(prepared.NewlyCreated.Marker)}\n";
    private static string Bool(bool value) => value ? "true" : "false";
    public void Execute(PreparedTarget prepared, string rootStatus, string lockStatus, string operation, Action<string> failpoint)
    {
        Authenticate(); var root = target.RuntimeRoot; var parentPath = Path.GetDirectoryName(root)!; Components(parentPath, false);
        var parent = Bind(parentPath, true, allowParent: true);
        var digest = prepared.MarkerDigest; var flags = prepared.NewlyCreated;
        var control = root + "/.hvo-deploy"; var marker = control + "/ownership";
        var lockPath = parentPath + "/" + SplitHostInventory.LockName(target); var statePath = lockPath + ".state";
        var rootBound = Resolve(root, parentPath + "/.hvo-deploy-cleanup-root-" + digest, flags.Root && rootStatus != "none", true);
        var controlBound = Resolve(control, root + "/.hvo-deploy.cleanup." + digest, flags.ControlDirectory && rootStatus != "none", true);
        var markerBound = Resolve(marker, control + "/.ownership.cleanup." + digest, flags.Marker && rootStatus != "none", false, Marker(digest));
        Require(controlBound is null || rootBound?.Path == root, "provenance-or-content-invalid");
        Require(markerBound is null || controlBound?.Path == control, "provenance-or-content-invalid");
        if (rootBound is not null)
        {
            var mounts = Encoding.UTF8.GetString(Convert.FromBase64String(files.Call("mounts")));
            Require(mounts.Length <= 1024 * 1024, "mount-table-too-large");
            foreach (var line in mounts.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split(' '); Require(fields.Length >= 6, "invalid-mount-table");
                var mount = fields[4].Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\012", "\n", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);
                Require(!Beneath(mount, root), "provenance-or-content-invalid");
            }
            Require(List(DescriptorPath(rootBound.Handle)).All(n => controlBound is not null && n == Path.GetFileName(controlBound.Path)), "provenance-or-content-invalid");
        }
        if (controlBound is not null) Require(List(DescriptorPath(controlBound.Handle)).All(n => markerBound is not null && n == Path.GetFileName(markerBound.Path)), "provenance-or-content-invalid");
        var lockBound = Resolve(lockPath, lockPath + ".cleanup." + digest, lockStatus != "none", false, Lock(digest));
        if (lockBound is not null) files.Call("lock", lockBound.Handle);
        var stateBound = Resolve(statePath, statePath + ".cleanup." + digest, lockStatus != "none", false, State(prepared));
        Require(stateBound is null || lockBound is not null, "provenance-or-content-invalid");
        if (operation == "cleanup-root")
        {
            Require(lockBound is not null && stateBound is not null, "provenance-or-content-invalid");
            Revalidate(parent); Revalidate(lockBound!); Revalidate(stateBound!);
            if (flags.Marker && markerBound is not null) { Revalidate(rootBound!); Revalidate(controlBound!); Remove(controlBound!, markerBound, "ownership", ".ownership.cleanup." + digest); }
            failpoint("marker");
            if (flags.ControlDirectory && controlBound is not null) { Revalidate(parent); Revalidate(rootBound!); Remove(rootBound!, controlBound, ".hvo-deploy", ".hvo-deploy.cleanup." + digest); }
            failpoint("control");
            if (flags.Root && rootBound is not null) { Revalidate(parent); Remove(parent, rootBound, Path.GetFileName(root), ".hvo-deploy-cleanup-root-" + digest); }
            failpoint("root");
        }
        if (operation == "cleanup-lock")
        {
            Require(!flags.Marker || Stat(marker) is null, "provenance-or-content-invalid");
            Require(!flags.ControlDirectory || Stat(control) is null, "provenance-or-content-invalid");
            Require(!flags.Root || Stat(root) is null, "provenance-or-content-invalid");
            if (stateBound is not null) { Revalidate(parent); Revalidate(lockBound!); Remove(parent, stateBound, Path.GetFileName(statePath), Path.GetFileName(statePath) + ".cleanup." + digest); }
            failpoint("lock-state");
            if (lockBound is not null) { Revalidate(parent); Remove(parent, lockBound, Path.GetFileName(lockPath), Path.GetFileName(lockPath) + ".cleanup." + digest); }
            failpoint("lock");
        }
        if (operation == "cleanup-root" || rootStatus == "completed") Require((!flags.Root || Stat(root) is null && (operation == "cleanup-root" || rootBound is null)) && (!flags.ControlDirectory || Stat(control) is null) && (!flags.Marker || Stat(marker) is null), "provenance-or-content-invalid");
        if (operation == "cleanup-lock" || lockStatus == "completed") Require(Stat(lockPath) is null && Stat(statePath) is null && (operation == "cleanup-lock" || lockBound is null && stateBound is null), "provenance-or-content-invalid");
    }
    private void Remove(Bound parent, Bound entry, string name, string quarantineName)
    {
        Revalidate(parent); Revalidate(entry);
        var basePath = DescriptorPath(parent.Handle); var source = basePath + "/" + name; var quarantine = basePath + "/" + quarantineName;
        if (Path.GetFileName(entry.Path) == name)
        {
            Require(Stat(quarantine) is null, "provenance-or-content-invalid");
            files.Call("move", source, quarantine);
            Require(Stat(source) is null, "provenance-or-content-invalid");
        }
        var parked = entry with { Path = quarantine };
        try { Revalidate(parked); }
        catch (InstallerException)
        {
            if (Stat(source) is null) files.Call("move", quarantine, source);
            throw;
        }
        // Empty-directory deletion is deliberate: recovery never traverses or recursively removes a runtime tree.
        files.Call(entry.Node.Directory ? "rmdir" : "unlink", quarantine);
        files.Call("sync", basePath);
    }
}
