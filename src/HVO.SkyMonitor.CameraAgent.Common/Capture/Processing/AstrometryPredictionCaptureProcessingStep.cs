using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Predicts catalog stars from capture-time rig and site facts without consuming the canonical render scene.</summary>
internal sealed class AstrometryPredictionCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    AstrometryPredictionCaptureProcessingStepOptions options,
    IProjectedSceneStagingStore stagingStore,
    IProjectedSceneStagingReader stagingReader,
    CameraAgentRecipeExecutionAdapter adapter,
    IServiceProvider services)
    : ProjectedSceneCaptureProcessingStep(metadata, new ProjectedSceneCaptureProcessingStepOptions
    {
        OutputVariant = options.OutputVariant ?? AliasVariant,
        MaximumMagnitude = options.MaximumMagnitude,
        MaximumResults = options.MaximumResults,
        AstronomyAlgorithmVersion = options.AstronomyAlgorithmVersion ?? ProjectedSceneCaptureProcessingStepOptions.DefaultAstronomyAlgorithmVersion
    }, stagingStore, stagingReader, adapter, services: services)
{
    internal const string Alias = "AstrometryPrediction";
    internal const string AliasVariant = "astrometry-prediction-v1";
    internal override bool IsAstrometryPrediction => true;
}

internal sealed class AstrometryPredictionCaptureProcessingStepOptions
{
    [MinLength(1), MaxLength(128)]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputVariant { get; init; }

    [Range(-30d, 30d)]
    public double MaximumMagnitude { get; init; } = 5;

    [Range(1, ProjectedSceneJson.MaximumObjectCount)]
    public int MaximumResults { get; init; } = 2000;

    // Preserve the algorithm identifier of an older single-scene configuration when it is split.
    [MinLength(1), MaxLength(128)]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AstronomyAlgorithmVersion { get; init; }
}
