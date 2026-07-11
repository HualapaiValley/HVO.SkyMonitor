using System;
using System.Collections.Generic;
using System.Text.Json;

namespace HVO.SkyMonitor.AgentCore;

public sealed record CameraModuleConfig(
    ObservatoryLocation Observatory,
    CameraModuleDescriptor Module,
    CameraRigConfig Rig,
    IReadOnlyList<CaptureProcessingStepConfig>? ProcessingSteps = null,
    CapturePipelineConfig? Pipeline = null,
    string? AgentId = null)
{
    public string ModuleType => Module?.Type ?? throw new InvalidOperationException("Camera module type must be specified.");

    public JsonElement? ModuleOptions => Module?.Options;

    public IReadOnlyList<CaptureProcessingStepConfig> ResolveProcessingSteps()
        => ProcessingSteps ?? Pipeline?.Steps ?? CapturePipelineConfig.Empty.Steps;
}

public sealed record CameraModuleDescriptor(
    string Type,
    JsonElement? Options = null);

public sealed record CapturePipelineConfig(
    IReadOnlyList<CaptureProcessingStepConfig> Steps)
{
    public static CapturePipelineConfig Empty { get; } = new(Array.Empty<CaptureProcessingStepConfig>());
}

public sealed record CaptureProcessingStepConfig(
    string Type,
    string? Id = null,
    int? Order = null,
    JsonElement? Options = null);
