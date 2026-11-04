using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;

namespace HVO.SkyMonitor.Deployment.Contracts;

/// <summary>The persistent-bootstrap continuity barrier exercised by preflight's historical scenario family.</summary>
public static class PreflightContinuity
{
    /// <returns>The existing bounded refusal code, or null when the retained continuity is sufficient.</returns>
    public static string? Validate(string mode, JsonObject local, JsonObject central)
    {
        ArgumentNullException.ThrowIfNull(local); ArgumentNullException.ThrowIfNull(central);
        if (mode != "persistent") return null;
        try
        {
            var durable = local["durable"]!; var device = local["deviceId"]!.GetValue<string>();
            var maximum = Number(central["maximumCaptureSequence"]); if (maximum is null) return "invalid-continuity";
            if (maximum > 0 || (central["centralFrameCount"]?.ToJsonString() ?? "0") != "0")
            {
                if (durable["rawIngressDatabaseExists"]?.GetValue<bool>() != true) return "persistent-local-capture-history-missing";
                var sequences = ((JsonArray)durable["captureSequences"]!).Where(n => n!["agentId"]!.GetValue<string>() == device).Select(n => Number(n!["lastSequence"])).ToArray();
                if (sequences.Length != 1 || sequences[0] is null || sequences[0] <= 0 || sequences[0] < maximum) return "persistent-local-capture-history-behind";
            }
            var artifacts = Number(central["centralArtifactCount"]); var artifactSequence = Maximum((JsonArray)central["latestArtifacts"]!, _ => true);
            if (artifacts is null || artifactSequence is null) return "invalid-continuity";
            if (artifacts > 0)
            {
                if (durable["artifactOutboxDatabaseExists"]?.GetValue<bool>() != true || !(Number(durable["artifactOutboxMaximumRecordId"]) > 0) || !(Number(durable["artifactOutboxMaximumAuditId"]) > 0)) return "persistent-local-artifact-history-missing";
                var localSequence = Maximum((JsonArray)durable["latestArtifacts"]!, n => n["status"]!.GetValue<string>() == "acknowledged" && n["agentId"]!.GetValue<string>() == device);
                if (localSequence is null || artifactSequence <= 0 || localSequence < artifactSequence) return "persistent-local-artifact-history-behind";
            }
            var fleet = central["fleetAgentInstanceId"]?.GetValue<string>() ?? ""; var fleetMaximum = Number(central["maximumHeartbeatSequence"]);
            if (fleetMaximum is null) return "invalid-continuity";
            if (fleetMaximum > 0 || fleet.Length > 0)
            {
                if (durable["fleetDatabaseExists"]?.GetValue<bool>() != true) return "persistent-local-fleet-history-missing";
                var localMaximum = Number(durable["fleetMaximumSequence"]); var next = Number(durable["fleetNextSequence"]);
                if (localMaximum is null || next is null) return "invalid-continuity";
                localMaximum = BigInteger.Max(localMaximum.Value, next.Value - 1);
                if (fleet.Length == 0 || durable["fleetAgentInstanceId"]?.GetValue<string>() != fleet) return "persistent-local-fleet-identity-mismatch";
                if (localMaximum < fleetMaximum || next <= fleetMaximum) return "persistent-local-fleet-history-behind";
            }
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or NullReferenceException or InvalidCastException) { return "invalid-continuity"; }
    }
    private static BigInteger? Maximum(JsonArray values, Func<JsonNode, bool> select)
    {
        BigInteger maximum = 0;
        foreach (var node in values.Where(n => n is not null && select(n)))
        {
            var value = Number(node!["captureSequence"]); if (value is null) return null; maximum = BigInteger.Max(maximum, value.Value);
        }
        return maximum;
    }
    private static BigInteger? Number(JsonNode? node)
    {
        var value = node?.ToJsonString() ?? "0";
        return value.Length > 0 && value.All(c => c is >= '0' and <= '9') && BigInteger.TryParse(value, CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}
