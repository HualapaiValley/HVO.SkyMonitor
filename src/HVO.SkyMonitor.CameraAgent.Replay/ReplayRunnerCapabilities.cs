using System.Diagnostics;
using System.Runtime.InteropServices;
using HVO.SkyMonitor.Processing;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.CameraAgent.Replay;

[JsonConverter(typeof(JsonStringEnumConverter<ReplayRunnerWarmupStatus>))]
public enum ReplayRunnerWarmupStatus
{
    Completed,
    NotApplicable,
    Unsupported,
    Failed
}

public sealed record ReplayRunnerWarmupStage(
    string Name,
    ReplayRunnerWarmupStatus Status,
    TimeSpan Elapsed,
    string Detail);

public sealed record ReplayRunnerWarmupStages(
    ReplayRunnerWarmupStage RuntimeJit,
    ReplayRunnerWarmupStage NativeLibraries,
    ReplayRunnerWarmupStage Catalog,
    ReplayRunnerWarmupStage Calibration,
    ReplayRunnerWarmupStage Models,
    ReplayRunnerWarmupStage Gpu)
{
    public static ReplayRunnerWarmupStages NotRun() => new(
        new("runtime-jit", ReplayRunnerWarmupStatus.NotApplicable, TimeSpan.Zero, "Warmup was not requested."),
        new("native-libraries", ReplayRunnerWarmupStatus.NotApplicable, TimeSpan.Zero, "No mandatory native transport library."),
        new("catalog", ReplayRunnerWarmupStatus.NotApplicable, TimeSpan.Zero, "Catalog access is outside the recipe-kernel boundary."),
        new("calibration", ReplayRunnerWarmupStatus.NotApplicable, TimeSpan.Zero, "Calibration inputs are supplied as artifacts."),
        new("models", ReplayRunnerWarmupStatus.Unsupported, TimeSpan.Zero, "No model runtime is used by built-in recipes."),
        new("gpu", ReplayRunnerWarmupStatus.Unsupported, TimeSpan.Zero, "GPU execution is not supported by this runner."));
}

public sealed record ReplayRunnerRecipeCapability(
    string Name,
    string SemanticVersion,
    string ImplementationVersion);

public sealed record ReplayRunnerCapabilities(
    int ProtocolVersion,
    string OperatingSystem,
    string OsArchitecture,
    string ProcessArchitecture,
    string RuntimeIdentifier,
    string FrameworkDescription,
    int ProcessId,
    DateTimeOffset ProcessStartedUtc,
    IReadOnlyList<ReplayRunnerRecipeCapability> BuiltInRecipes,
    int MaxConcurrency,
    long MaxTransferBytes,
    ReplayRunnerWarmupStages Warmup)
{
    private static readonly string[] BuiltInRecipeNames =
    [
        BuiltInProcessingRecipes.LinearNormalization,
        BuiltInProcessingRecipes.EncodedPreview,
        BuiltInProcessingRecipes.JpegEncoding,
        BuiltInProcessingRecipes.Annotation,
        BuiltInProcessingRecipes.RollingMean,
        BuiltInProcessingRecipes.ImageQuality,
        BuiltInProcessingRecipes.NoOpAnalyzer,
        BuiltInProcessingRecipes.CloudAssessment,
        BuiltInProcessingRecipes.WeatherCloudOverlay,
        BuiltInProcessingRecipes.ReferenceCalibration,
        BuiltInProcessingRecipes.ProjectedScene
    ];

    public static ReplayRunnerCapabilities Create(
        LocalReplayRunnerOptions options,
        ReplayRunnerWarmupStages? warmup = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(requireAuthentication: false);
        var recipes = BuiltInRecipeNames.Select(static name =>
        {
            if (!BuiltInProcessingRecipes.TryGetDefinition(name, out var definition) || definition is null)
            {
                throw new InvalidOperationException($"Built-in recipe '{name}' has no definition.");
            }
            return new ReplayRunnerRecipeCapability(
                definition.Name,
                definition.SemanticVersion,
                definition.ImplementationVersion);
        }).ToArray();
        using var process = Process.GetCurrentProcess();
        return new ReplayRunnerCapabilities(
            ReplayProtocol.Version,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.RuntimeIdentifier,
            RuntimeInformation.FrameworkDescription,
            Environment.ProcessId,
            process.StartTime.ToUniversalTime(),
            recipes,
            options.MaxConcurrency,
            options.MaxTotalTransferBytes,
            warmup ?? ReplayRunnerWarmupStages.NotRun());
    }
}
