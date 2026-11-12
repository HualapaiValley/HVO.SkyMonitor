using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Graph revisions cannot override the profile that also owns retention and outbox draining.</summary>
public static class CaptureStoragePolicy
{
    public const string RejectionMessage = "Enabled Storage settings must match the active capture profile. Pause acquisition and finish in-flight processing before changing the local capture profile, then reconcile, validate and activate the graph before resuming. Raw captures are retained while incompatible standard processing is skipped.";

    public const string LiveMismatchLaneReason = "storage-policy-mismatch";

    public const string SceneUnavailableLaneReason = "storage-policy-mismatch-scene-unavailable";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static void EnsureMatchesProfile(CapturePipelineConfig candidate, CapturePipelineConfig? profile)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var enabled = candidate.Steps.Where(IsEnabledStorage).ToArray();
        if (enabled.Length == 0) return;
        if (profile is null) throw Mismatch(["StorageNode"]);
        var local = profile.Steps.Where(IsEnabledStorage).ToDictionary(NodeId, StringComparer.OrdinalIgnoreCase);
        var differences = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var step in enabled)
        {
            if (!local.TryGetValue(NodeId(step), out var policy))
            {
                differences.Add("StorageNode");
                continue;
            }
            var candidatePolicy = EffectivePolicy(step);
            var profilePolicy = EffectivePolicy(policy);
            foreach (var field in candidatePolicy.EnumerateObject())
                if (field.Value.GetRawText() != profilePolicy.GetProperty(field.Name).GetRawText())
                    differences.Add(field.Name);
        }
        if (differences.Count > 0) throw Mismatch(differences);
    }

    private static ValidationException Mismatch(IEnumerable<string> fields)
        => new(new ValidationResult(RejectionMessage, fields), null, null);

    /// <summary>Safe field names only; paths and policy values never appear in operator diagnostics.</summary>
    public static string DescribeMismatch(ValidationException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var fields = (exception.ValidationResult?.MemberNames ?? [])
            .Where(static field => field is "StorageNode" or "StorageRoot" or "RetentionDays" or
                "UpdateLatestFrame" or "QueueForUpload" or "Policies").Distinct(StringComparer.Ordinal).ToArray();
        return RejectionMessage + (fields.Length > 0 ? " Differing fields: " + string.Join(", ", fields) + "." : string.Empty);
    }

    private static bool IsEnabledStorage(CaptureProcessingStepConfig step)
        => step.Enabled != false && step.Type.Equals(FileStorageCaptureProcessingStep.StableAlias, StringComparison.OrdinalIgnoreCase);

    private static string NodeId(CaptureProcessingStepConfig step)
        => string.IsNullOrWhiteSpace(step.Id) ? step.Type : step.Id.Trim();

    private static JsonElement EffectivePolicy(CaptureProcessingStepConfig step)
    {
        // Bind with the same defaults, casing and enum/numeric rules as the runtime. Do not rewrite source JSON.
        var options = step.Options is { } source
            ? JsonSerializer.Deserialize<FileStorageCaptureProcessingStepOptions>(source.GetRawText(), SerializerOptions)
            : null;
        options ??= new FileStorageCaptureProcessingStepOptions();
        return JsonSerializer.SerializeToElement(new
        {
            StorageRoot = NormalizeRoot(options.StorageRoot),
            options.RetentionDays,
            options.UpdateLatestFrame,
            options.QueueForUpload,
            // Equal-specificity overlapping selectors use the first match at runtime, so order is significant.
            Policies = (options.Policies ?? []).Select(static policy => new
            {
                StepId = policy.StepId?.ToUpperInvariant(),
                policy.Role,
                policy.Variant,
                policy.RecipeName,
                policy.QueueForUpload,
                policy.RetentionDays
            }).ToArray()
        });
    }

    private static string NormalizeRoot(string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }
}
