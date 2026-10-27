using System.Globalization;
using System.Text.Json.Nodes;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment;

internal sealed record DownContinuity(string Device, long Sequence, string ConfigurationHash);
internal sealed record DownPause(long Version, DateTimeOffset Completed);
internal sealed record DownSample(DateTimeOffset Captured, string Fingerprint);

/// <summary>The retained pause/continuity/drain proof. No network or filesystem authority lives here.</summary>
internal static class DownObservation
{
    private static readonly string[] Counters = ["pendingCount", "leasedCount", "retryCount", "quarantineCount", "terminalCount"];
    private static readonly string[] LaneCounters = ["pendingCount", "leasedCount", "retryCount", "quarantineCount", "pressureLevel"];
    private static readonly string[] Sections = ["configuration", "captureControl", "rawIngress", "captureLanes", "captureProcessing", "artifactOutbox", "transientWorker"];
    private static InstallerException Invalid(string code) => new("down: " + code);
    internal static long Whole(JsonNode? value)
    {
        if (value is not JsonValue number || !decimal.TryParse(number.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || parsed < 0 || decimal.Truncate(parsed) != parsed || parsed > long.MaxValue) throw Invalid("invalid-counter");
        return (long)parsed;
    }
    internal static DateTimeOffset Utc(string value)
    {
        if (!Match(value, "[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:[.][0-9]+)?(?:Z|[+]00:00)") || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) throw Invalid("invalid-timestamp");
        return parsed;
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "The retained shell continuity contract normalizes SHA-256 to lowercase.")]
    public static DownContinuity Continuity(JsonObject value, string? expected = null)
    {
        try
        {
            var device = value.Text("deviceId"); var durable = value["durable"]!;
            var matches = durable.Array("captureSequences").Where(n => n!.Text("agentId") == device).ToArray();
            if (device.Length == 0 || expected is not null && device != expected || value.Text("configuredAgentId") != device || !value.Flag("isProvisioned") || !durable.Flag("rawIngressDatabaseExists") || matches.Length != 1) throw Invalid("invalid-continuity-boundary");
            Keys(matches[0]!, "agentId lastSequence"); var hash = value.Text("activeConfigurationSha256");
            if (!Match(hash, "[0-9A-Fa-f]{64}")) throw Invalid("invalid-continuity-boundary");
            return new(device, Whole(matches[0]!["lastSequence"]), hash.ToLowerInvariant());
        }
        catch (Exception e) when (e is InstallerException or InvalidOperationException or FormatException or NullReferenceException) { throw Invalid("invalid-continuity-boundary"); }
    }
    public static DownPause Pause(JsonObject value, long? expected = null)
    {
        try
        {
            Keys(value, "state version changed replayed requestedUtc completedUtc");
            var state = value["state"]!; var version = Whole(value["version"]); var requested = Utc(value.Text("requestedUtc")); var completed = Utc(value.Text("completedUtc"));
            if (!(state.GetValueKind() == System.Text.Json.JsonValueKind.Number && Whole(state) == 3 || state.GetValueKind() == System.Text.Json.JsonValueKind.String && state.GetValue<string>() == "Paused") || expected is not null && version != expected || requested > completed) throw Invalid("invalid-pause-receipt");
            _ = value.Flag("changed"); _ = value.Flag("replayed"); return new(version, completed);
        }
        catch (Exception e) when (e is InstallerException or InvalidOperationException or FormatException or NullReferenceException) { throw Invalid("invalid-pause-receipt"); }
    }
    public static DownSample? Safe(JsonObject summary, DownPause pause, DownContinuity boundary, string mode)
    {
        try
        {
            var captured = Utc(summary.Text("capturedUtc")); if (captured < pause.Completed) return null;
            foreach (var name in new[] { "rawIngress", "captureLanes", "captureProcessing", "artifactOutbox" })
            {
                var section = summary[name]!; var observed = Utc(section.Text("observedUtc"));
                if (section.Text("freshness") != "fresh" || observed > captured || name is "rawIngress" or "captureLanes" && observed < pause.Completed) return null;
            }
            var control = summary["captureControl"]!; var worker = summary["transientWorker"]!;
            if (control.Text("freshness") is not ("fresh" or "stale") || Utc(control.Text("observedUtc")) > captured || Utc(worker.Text("observedUtc")) > captured) return null;
            var configuration = summary["configuration"]!["value"]!;
            if (!configuration.Flag("isCurrent") || configuration.Text("validationStatus") != "validated" || configuration.Text("agentId") != boundary.Device || configuration.Text("moduleType").Length == 0 || configuration.Text("transientDetection") != mode) return null;
            if (control["value"]!.Text("state") != "Paused" || Whole(control["value"]!["version"]) != pause.Version || !control["value"]!.Flag("isInitialized")) return null;
            if (!Queues(summary, boundary, mode)) return null;
            if (mode == "Off" ? worker.Text("freshness") is not ("disabled" or "fresh" or "stale") : worker.Text("freshness") != "fresh") return null;
            var projection = new JsonObject(Sections.Select(name => KeyValuePair.Create(name, summary[name]!["value"]?.DeepClone())));
            return new(captured, Hash(Canonical(projection) + "\n"));
        }
        catch (Exception e) when (e is InstallerException or InvalidOperationException or FormatException or NullReferenceException or OverflowException) { return null; }
    }
    private static bool Queues(JsonObject doc, DownContinuity boundary, string mode)
    {
        var raw = doc["rawIngress"]!["value"]!; var lanes = doc["captureLanes"]!["value"]!; var processing = doc["captureProcessing"]!["value"]!; var outbox = doc["artifactOutbox"]!["value"]!; var worker = doc["transientWorker"]!["value"]!;
        foreach (var queue in new[] { raw, processing, outbox }) foreach (var key in Counters) _ = Whole(queue[key]);
        foreach (var key in LaneCounters.Where(key => key != "pressureLevel")) _ = Whole(lanes[key]);
        var children = lanes.Array("lanes"); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lane in children)
        {
            if (lane!.Text("name").Length == 0 || !names.Add(lane.Text("name"))) return false;
            _ = lane.Flag("required"); foreach (var key in LaneCounters) _ = Whole(lane![key]);
            foreach (var identity in lane.Array("pendingCaptures")) { Keys(identity!, "agentId captureSequence"); if (identity!.Text("agentId").Length == 0) return false; _ = Whole(identity!["captureSequence"]); }
        }
        var maximum = Whole(worker["maximumCandidates"]); if (maximum is < 1 or > 64 || Whole(worker["pendingFrames"]) != 0 || Whole(worker["pendingCandidates"]) != 0) return false;
        if (raw.Text("availability") != "Accepting" || lanes.Text("availability") != "Healthy" || processing.Text("availability") != "Healthy" || outbox.Text("availability") != "Healthy") return false;
        foreach (var queue in new[] { raw, processing, outbox }) foreach (var key in Counters.Where(key => key != "pendingCount")) if (Whole(queue[key]) != 0) return false;
        if (Whole(processing["pendingCount"]) != 0 || Whole(outbox["pendingCount"]) != 0) return false;
        foreach (var key in LaneCounters.Where(key => key != "pressureLevel")) if (Whole(lanes[key]) != children.Sum(lane => Whole(lane![key]))) return false;
        if (Whole(lanes["leasedCount"]) != 0 || Whole(lanes["retryCount"]) != 0 || Whole(lanes["quarantineCount"]) != 0) return false;
        var transient = children.Where(lane => lane!.Text("name") == "transient" && lane.Flag("required")).ToArray();
        bool Empty(JsonNode lane) => LaneCounters.All(key => Whole(lane![key]) == 0) && lane.Array("pendingCaptures").Count == 0;
        if (mode == "Off") return transient.Length == 0 && worker.Text("availability") == "Disabled" && Whole(raw["pendingCount"]) == 0 && Whole(lanes["pendingCount"]) == 0 && children.All(lane => Empty(lane!));
        if (mode != "Hybrid" || transient.Length != 1 || worker.Text("availability") != "Healthy") return false;
        var tail = Math.Min(boundary.Sequence, 2); var pending = Whole(transient[0]!["pendingCount"]);
        if (Whole(lanes["pendingCount"]) != pending || Whole(raw["pendingCount"]) < tail || Whole(raw["pendingCount"]) > tail * 3 || pending < tail || pending > tail * (1 + maximum)) return false;
        if (LaneCounters.Skip(1).Any(key => Whole(transient[0]![key]) != 0) || !children.Where(lane => lane!.Text("name") != "transient").All(lane => Empty(lane!))) return false;
        var identities = transient[0]!.Array("pendingCaptures");
        return identities.Count == tail && identities.Select((n, i) => n!.Text("agentId") == boundary.Device && Whole(n!["captureSequence"]) == boundary.Sequence - tail + 1 + i).All(b => b);
    }
}
