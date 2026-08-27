using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.AgentCore;

public sealed record CameraModuleConfig(
    ObservatoryLocation Observatory,
    CameraModuleDescriptor Module,
    CameraRigConfig Rig,
    [property: JsonRequired] CapturePipelineConfig Pipeline,
    string? AgentId = null)
{
    /// <summary>Gets the validated immutable deployment location selected for this process lifetime.</summary>
    [JsonIgnore]
    public DeploymentLocationSnapshot? DeploymentLocation { get; init; }

    /// <summary>Gets whether durable replay intentionally removed precise deployment coordinates.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DeploymentLocationRedacted { get; init; }

    /// <summary>Gets the optional local capture schedule carried by this immutable configuration revision.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CaptureScheduleDefinition? Schedule { get; init; }

    /// <summary>Gets the active coordinates, preferring the validated versioned snapshot.</summary>
    public ObservatoryLocation ResolveObservatory(DateTimeOffset? effectiveUtc = null)
    {
        if (DeploymentLocation is not { } location)
        {
            return Observatory;
        }
        if (effectiveUtc.HasValue && !location.IsEffectiveAt(effectiveUtc.Value))
        {
            throw new InvalidOperationException("Deployment location is not effective for the capture time.");
        }
        return location.ToObservatoryLocation();
    }

    public string ModuleType => Module?.Type ?? throw new InvalidOperationException("Camera module type must be specified.");

    public JsonElement? ModuleOptions => Module?.Options;

}

public sealed record CameraModuleDescriptor(
    string Type,
    JsonElement? Options = null);

public sealed record CapturePipelineConfig(
    [property: JsonRequired] IReadOnlyList<CaptureProcessingStepConfig> Steps,
    [property: JsonRequired] string SchemaVersion = CapturePipelineSchemaVersions.ExplicitV2,
    [property: JsonRequired]
    CapturePipelineDependencyPolicy DependencyPolicy = CapturePipelineDependencyPolicy.RejectEnabledDependent)
{
    public static CapturePipelineConfig Empty { get; } = new(Array.Empty<CaptureProcessingStepConfig>());
}

public static class CapturePipelineSchemaVersions
{
    public const string LegacyV1 = "cameraagent-capture-pipeline-v1";
    public const string ExplicitV2 = "cameraagent-capture-pipeline-v2";
}

public enum CapturePipelineDependencyPolicy
{
    [System.Text.Json.Serialization.JsonStringEnumMemberName("legacy-inference-v1")]
    LegacyInference,

    [System.Text.Json.Serialization.JsonStringEnumMemberName("reject-enabled-dependent-v1")]
    RejectEnabledDependent
}

public sealed record CaptureProcessingStepConfig(
    string Type,
    string? Id = null,
    int? Order = null,
    JsonElement? Options = null,
    IReadOnlyList<string>? DependsOn = null,
    bool Required = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Enabled = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CaptureProcessingPublicationPolicy? Publication = null);

public sealed record CaptureProcessingPublicationPolicy(
    [property: JsonRequired] CaptureProcessingPersistenceMode Persistence);

public enum CaptureProcessingPersistenceMode
{
    [JsonStringEnumMemberName("memory-only")]
    MemoryOnly,

    [JsonStringEnumMemberName("durable-local")]
    DurableLocal
}
