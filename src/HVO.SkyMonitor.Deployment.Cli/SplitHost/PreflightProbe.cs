using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment.Contracts;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment;

/// <summary>Read-only target policy; the host seam supplies facts, never a deployment phase.</summary>
internal sealed class PreflightProbe(IDeploymentHost host, Action<string, string, string, string, string> record)
{
    private void Check(string target, string check, bool passed, string success, string failure, string stage = "preflight")
    {
        record(stage, target, check, passed ? "passed" : "failed", passed ? success : failure);
        if (!passed) throw Error("preflight-check-failed");
    }
    public async Task<JsonObject> TargetAsync(DeploymentTarget target, JsonNode definition, BigInteger minimumMemory, CancellationToken token)
    {
        PreflightHostObservation? observation = null; string failure = "unreachable-or-invalid-response";
        try
        {
            using var files = host.OpenPreflight(target, token);
            var facts = files.Call("facts").Split('\t');
            if (facts.Length == 9)
            {
                var safety = RuntimeRoot(files, target.RuntimeRoot);
                var free = files.Call("space", safety.Ancestor).Trim();
                var sockets = "none";
                foreach (var port in definition.Array("ports"))
                {
                    var socketResult = files.Call("sockets", port!.ToJsonString());
                    if (socketResult == "unsupported") { sockets = "unsupported"; break; }
                    if (socketResult == "present") sockets = "present";
                    else if (socketResult != "none") { sockets = "unsupported"; break; }
                }
                observation = PreflightHostObservation.Parse(string.Join('\t', facts[0], facts[1], PreflightHostObservation.DockerArchitecture(facts[3]) ?? facts[3], facts[4], facts[5], free, facts[6], facts[7], facts[8], safety.Status + ":" + sockets));
            }
        }
        catch (InstallerException exception) { if (exception.Message.EndsWith("tool-unavailable", StringComparison.Ordinal)) failure = "tool-unavailable"; }
        catch (IOException) { }
        Check(target.Name, "ssh", observation is not null, "reachable", failure, recordSuccess: false);
        var value = observation!;
        Check(target.Name, "host-identity", value.HostIdentity == target.HostIdentity, "", "mismatch", recordSuccess: false);
        Check(target.Name, "host-name", value.HostName == target.HostName, "", "mismatch", recordSuccess: false);
        // The shell records SSH success only after shape and host identity validation.
        Check(target.Name, "ssh", value.Valid, "reachable", "invalid-response");
        Check(target.Name, "host-architecture", value.Architecture == target.Architecture, "", "mismatch", recordSuccess: false);
        Check(target.Name, "host-capacity", minimumMemory <= 0 || value.MeetsMemory(minimumMemory), minimumMemory > 0 ? "meets-sql-memory-limit" : "observed", "below-sql-memory-limit");
        record("preflight", target.Name, "clock", "passed", "observed");
        foreach (var (check, report) in new[] { ("thermal", value.ThermalReport), ("throttle", value.ThrottleReport) })
            record("preflight", target.Name, check, report.StartsWith("reported:", StringComparison.Ordinal) ? "passed" : "unsupported", report.StartsWith("reported:", StringComparison.Ordinal) ? "reported" : "unsupported");
        Check(target.Name, "runtime-root", value.RootStatus != "unsafe", value.RootStatus == "ready" ? "ready" : "needs-prepare", "unsafe");
        Check(target.Name, "ports", value.SocketStatus != "unsupported", "", "inspector-unavailable", recordSuccess: false);
        Check(target.Name, "ports", value.SocketStatus != "present", "no-conflict", "conflict");

        var context = await host.DockerAsync(["context", "inspect", "--format", "{{.Name}}", target.DockerContext], token).ConfigureAwait(false);
        Check(target.Name, "docker-context", context.ExitCode == 0, "", "unavailable", recordSuccess: false);
        Check(target.Name, "docker-context", context.StandardOutput.TrimEnd('\n') == target.DockerContext, "", "mismatch", recordSuccess: false);
        var result = await host.DockerAsync(["--context", target.DockerContext, "info", "--format", "{\"ID\":{{json .ID}},\"Name\":{{json .Name}},\"Architecture\":{{json .Architecture}},\"OSType\":{{json .OSType}},\"ServerVersion\":{{json .ServerVersion}}}"], token).ConfigureAwait(false);
        Check(target.Name, "docker-daemon", result.ExitCode == 0, "", "unreachable", recordSuccess: false);
        JsonObject? info = null;
        try { info = Parse(result.StandardOutput); } catch (System.Text.Json.JsonException) { } catch (InstallerException) { }
        string? Field(string name) => info?[name] is JsonValue field && field.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
        var id = Field("ID"); var name = Field("Name"); var rawArch = Field("Architecture");
        Check(target.Name, "docker-daemon", id is not null && name is not null && rawArch is not null, "", "invalid-response", recordSuccess: false);
        var arch = PreflightHostObservation.DockerArchitecture(rawArch!);
        Check(target.Name, "docker-daemon", arch is not null, "", "unsupported-architecture", recordSuccess: false);
        var os = Field("OSType"); var version = Field("ServerVersion");
        Check(target.Name, "docker-daemon", os is not null && version is not null && PreflightHostObservation.DockerVersion(version), "", "invalid-response", recordSuccess: false);
        Check(target.Name, "ssh-docker-correlation", id == target.DockerDaemonIdentity && name == value.HostName && name == target.HostName && arch == target.Architecture && os == "linux", "", "mismatch", recordSuccess: false);
        record("preflight", target.Name, "docker-daemon", "passed", "reachable");
        var compose = await host.DockerAsync(["--context", target.DockerContext, "compose", "version", "--short"], token).ConfigureAwait(false);
        Check(target.Name, "docker-compose", compose.ExitCode == 0, "", "unavailable", recordSuccess: false);
        var composeVersion = compose.StandardOutput.TrimEnd('\n');
        Check(target.Name, "docker-compose", PreflightHostObservation.ComposeVersion(composeVersion), "available", "invalid-version");
        foreach (var port in definition.Array("ports"))
        {
            var containers = await host.DockerAsync(["--context", target.DockerContext, "ps", "--quiet", "--filter", "publish=" + port!.ToJsonString()], token).ConfigureAwait(false);
            Check(target.Name, "containers", containers.ExitCode == 0, "", "query-failed", recordSuccess: false);
            Check(target.Name, "containers", containers.StandardOutput.TrimEnd('\n').Length == 0, "", "port-conflict", recordSuccess: false);
        }
        record("preflight", target.Name, "containers", "passed", "no-conflict");
        return new JsonObject
        {
            ["name"] = target.Name,
            ["hostIdentity"] = value.HostIdentity,
            ["hostName"] = value.HostName,
            ["dockerDaemonIdentity"] = id,
            ["architecture"] = value.Architecture,
            ["runtimeRoot"] = target.RuntimeRoot,
            ["deploymentState"] = value.RootStatus,
            ["dockerVersion"] = version,
            ["dockerComposeVersion"] = composeVersion,
            ["cpuCount"] = value.CpuCount,
            ["memoryKiB"] = value.MemoryKiB,
            ["freeSpaceKiB"] = value.FreeSpaceKiB,
            ["clockOffsetSeconds"] = (BigInteger.Parse(value.Clock, CultureInfo.InvariantCulture) - DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(CultureInfo.InvariantCulture),
            ["thermalReport"] = value.ThermalReport,
            ["throttleReport"] = value.ThrottleReport
        };
    }
    private void Check(string target, string check, bool passed, string success, string failure, bool recordSuccess)
    {
        if (!passed || recordSuccess) Check(target, check, passed, success, failure);
    }
    internal static (string Status, string Ancestor) RuntimeRoot(IDeploymentFiles files, string root)
    {
        var current = ""; var ancestor = "/"; var status = "ready";
        foreach (var component in root.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + component; var node = DeploymentNode.Parse(files.Call("stat", current));
            if (node is null) { status = "needs_prepare"; break; }
            ancestor = current;
            if (!node.Directory) { status = "unsafe"; break; }
        }
        var owner = files.Call("uid"); var metadata = DeploymentNode.Parse(files.Call("stat", ancestor));
        if (metadata is null || (metadata.Mode & 18) != 0 || metadata.Owner != 0 && metadata.Owner.ToString(CultureInfo.InvariantCulture) != owner) status = "unsafe";
        else if (status == "ready" && metadata.Owner == 0 && owner != "0") status = "needs_prepare";
        return (status, ancestor);
    }
    public void Connectivity(DeploymentTarget target, string check, string operation, CancellationToken token, params string[] arguments)
    {
        string result;
        try { using var files = host.OpenPreflight(target, token); result = files.Call(operation, arguments); }
        catch (InstallerException exception) { result = exception.Message.EndsWith("tool-unavailable", StringComparison.Ordinal) ? "tool-unavailable" : "unreachable"; }
        catch (IOException) { result = "unreachable"; }
        Check(target.Name, check, result == "reachable", "reachable", result == "tool-unavailable" ? result : "unreachable", "connectivity");
    }
}
