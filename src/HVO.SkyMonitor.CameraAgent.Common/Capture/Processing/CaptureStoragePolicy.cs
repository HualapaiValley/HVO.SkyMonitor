using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Graph revisions cannot override the profile that also owns retention and outbox draining.</summary>
public static class CaptureStoragePolicy
{
    public const string RejectionMessage = "Enabled Storage settings must match the active capture profile. Change Storage through the local capture-profile configuration owner, then reconcile this graph with that policy.";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static void EnsureMatchesProfile(CapturePipelineConfig candidate, CapturePipelineConfig? profile)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var enabled = candidate.Steps.Where(IsEnabledStorage).ToArray();
        if (enabled.Length == 0) return;
        if (profile is null) throw new ValidationException(RejectionMessage);
        var local = profile.Steps.Where(IsEnabledStorage).ToDictionary(NodeId, StringComparer.OrdinalIgnoreCase);
        foreach (var step in enabled)
        {
            if (!local.TryGetValue(NodeId(step), out var policy) ||
                !string.Equals(EffectivePolicy(step), EffectivePolicy(policy), StringComparison.Ordinal))
                throw new ValidationException(RejectionMessage);
        }
    }

    private static bool IsEnabledStorage(CaptureProcessingStepConfig step)
        => step.Enabled != false && step.Type.Equals(FileStorageCaptureProcessingStep.StableAlias, StringComparison.OrdinalIgnoreCase);

    private static string NodeId(CaptureProcessingStepConfig step)
        => string.IsNullOrWhiteSpace(step.Id) ? step.Type : step.Id.Trim();

    private static string EffectivePolicy(CaptureProcessingStepConfig step)
    {
        // Bind with the same defaults, casing and enum/numeric rules as the runtime. Do not rewrite source JSON.
        var options = step.Options is { } source
            ? JsonSerializer.Deserialize<FileStorageCaptureProcessingStepOptions>(source.GetRawText(), SerializerOptions)
            : null;
        return CaptureContractJson.ComputeCanonicalJsonSha256(options ?? new FileStorageCaptureProcessingStepOptions());
    }
}
