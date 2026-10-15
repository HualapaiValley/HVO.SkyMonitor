using System.Text.Json;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.LogicHost.Services;

/// <summary>Read-only validation of the published frozen-plan document; no CameraAgent code dependency.</summary>
internal static class ExecutionEvidencePlanValidator
{
    public static ExecutionEvidenceValidationResult ValidateRevision(GraphRevisionEvidenceV1 revision)
    {
        var plan = revision.FrozenPlan;
        if (plan.ValueKind != JsonValueKind.Object ||
            !Matches(plan, "schemaVersion", "cameraagent-processing-frozen-plan-v1") ||
            !Matches(plan, "revisionId", revision.RevisionId) ||
            !Matches(plan, "definitionIdentitySha256", revision.DefinitionIdentitySha256) ||
            !Matches(plan, "sharedPlanIdentitySha256", revision.SharedPlanIdentitySha256) ||
            !Matches(plan, "localPlanIdentitySha256", revision.LocalPlanIdentitySha256) ||
            !plan.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array ||
            nodes.GetArrayLength() is < 1 or > GraphExecutionEvidenceLimits.MaximumNodeCount)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidBody, "graphRevision.frozenPlan");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("nodeId", out var id) ||
                id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) ||
                id.GetString()!.Length > GraphExecutionEvidenceLimits.MaximumIdentifierLength || !ids.Add(id.GetString()!) ||
                !node.TryGetProperty("required", out var required) || required.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !node.TryGetProperty("planSha256", out var hash) || hash.ValueKind != JsonValueKind.String || !IsHash(hash.GetString()))
            {
                return Failure(GraphExecutionEvidenceReasonCodes.InvalidNode, "graphRevision.frozenPlan.nodes");
            }
        }
        // Local hashes include non-exported publication settings. These identities are compared, never recomputed.
        return ExecutionEvidenceValidationResult.Success;
    }

    public static ExecutionEvidenceValidationResult ValidateExecution(GraphExecutionEvidenceV1 execution, GraphRevisionEvidenceV1 revision)
    {
        if (execution.DefinitionIdentitySha256 != revision.DefinitionIdentitySha256 ||
            execution.SharedPlanIdentitySha256 != revision.SharedPlanIdentitySha256 ||
            execution.LocalPlanIdentitySha256 != revision.LocalPlanIdentitySha256)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidHash, "execution.graphRevisionId");
        }
        var nodes = revision.FrozenPlan.GetProperty("nodes").EnumerateArray()
            .ToDictionary(n => n.GetProperty("nodeId").GetString()!, StringComparer.Ordinal);
        if (nodes.Count != execution.Nodes.Length) { return Failure(GraphExecutionEvidenceReasonCodes.InvalidNode, "execution.nodes"); }
        foreach (var node in execution.Nodes)
        {
            if (!nodes.TryGetValue(node.NodeId, out var frozen) || !Matches(frozen, "planSha256", node.PlanSha256) ||
                frozen.GetProperty("required").GetBoolean() != node.Required)
            {
                return Failure(GraphExecutionEvidenceReasonCodes.InvalidNode, "execution.nodes");
            }
        }
        return ExecutionEvidenceValidationResult.Success;
    }

    private static bool Matches(JsonElement root, string name, string expected)
        => root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() == expected;
    internal static bool IsHash(string? value)
        => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static ExecutionEvidenceValidationResult Failure(string reason, string path)
        => ExecutionEvidenceValidationResult.Failure(reason, path);
}
