using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.ProcessingRunner.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<ProcessingRunnerWarmupStatus>))]
public enum ProcessingRunnerWarmupStatus
{
    Completed,
    NotApplicable,
    Unsupported,
    Failed
}

public sealed record ProcessingRunnerWarmupStage(
    string Name,
    ProcessingRunnerWarmupStatus Status,
    TimeSpan Elapsed,
    string Detail);

/// <summary>
/// Separately measured initialization stages so scheduling can account for startup cost. The stage set mirrors the
/// CameraAgent local replay runner so both runner families report comparable warm-up evidence.
/// </summary>
public sealed record ProcessingRunnerWarmupStages(
    ProcessingRunnerWarmupStage RuntimeJit,
    ProcessingRunnerWarmupStage NativeLibraries,
    ProcessingRunnerWarmupStage Catalog,
    ProcessingRunnerWarmupStage Calibration,
    ProcessingRunnerWarmupStage Models,
    ProcessingRunnerWarmupStage Gpu)
{
    public static ProcessingRunnerWarmupStages NotRun() => new(
        new("runtime-jit", ProcessingRunnerWarmupStatus.NotApplicable, TimeSpan.Zero, "Warmup was not requested."),
        new("native-libraries", ProcessingRunnerWarmupStatus.NotApplicable, TimeSpan.Zero, "No mandatory native transport library."),
        new("catalog", ProcessingRunnerWarmupStatus.NotApplicable, TimeSpan.Zero, "Catalog access is outside the recipe-kernel boundary."),
        new("calibration", ProcessingRunnerWarmupStatus.NotApplicable, TimeSpan.Zero, "Calibration inputs are supplied as artifacts."),
        new("models", ProcessingRunnerWarmupStatus.Unsupported, TimeSpan.Zero, "No model runtime is used by built-in recipes."),
        new("gpu", ProcessingRunnerWarmupStatus.Unsupported, TimeSpan.Zero, "GPU execution is not supported by this runner."));

    public bool IsWarm
        => RuntimeJit.Status == ProcessingRunnerWarmupStatus.Completed
            && NativeLibraries.Status == ProcessingRunnerWarmupStatus.Completed;

    public IEnumerable<ProcessingRunnerWarmupStage> All()
    {
        yield return RuntimeJit;
        yield return NativeLibraries;
        yield return Catalog;
        yield return Calibration;
        yield return Models;
        yield return Gpu;
    }
}

public sealed record ProcessingRunnerRecipeCapability(
    string Name,
    string SemanticVersion,
    string ImplementationVersion);

/// <summary>
/// Everything a runner advertises for matching: platform, resources, resource/latency classes, free-form labels, the
/// exact built-in recipe versions it executes, concurrency, transfer limits, and warm-up state.
/// </summary>
public sealed record ProcessingRunnerCapabilities(
    int ProtocolVersion,
    string OperatingSystem,
    string OsArchitecture,
    string ProcessArchitecture,
    string RuntimeIdentifier,
    string FrameworkDescription,
    int ProcessorCount,
    long TotalMemoryBytes,
    string ResourceClass,
    bool GpuAvailable,
    string LatencyClass,
    IReadOnlyList<string> Labels,
    IReadOnlyList<ProcessingRunnerRecipeCapability> BuiltInRecipes,
    int MaxConcurrency,
    long MaxTransferBytes,
    ProcessingRunnerWarmupStages Warmup,
    ProcessingRunnerWarmState WarmState)
{
    public const string DefaultResourceClass = "standard";

    public const string DefaultLatencyClass = "background";

    public static readonly string[] BuiltInRecipeNames =
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

    /// <summary>Creates the capability set of the current process for the built-in recipe kernel.</summary>
    public static ProcessingRunnerCapabilities CreateForCurrentProcess(
        int maxConcurrency,
        long maxTransferBytes,
        string? resourceClass,
        string? latencyClass,
        IReadOnlyList<string>? labels,
        ProcessingRunnerWarmupStages? warmup,
        bool gpuAvailable = false)
    {
        var recipes = BuiltInRecipeNames.Select(static name =>
        {
            if (!BuiltInProcessingRecipes.TryGetDefinition(name, out var definition) || definition is null)
            {
                throw new InvalidOperationException($"Built-in recipe '{name}' has no definition.");
            }
            return new ProcessingRunnerRecipeCapability(
                definition.Name,
                definition.SemanticVersion,
                definition.ImplementationVersion);
        }).ToArray();
        var stages = warmup ?? ProcessingRunnerWarmupStages.NotRun();
        var capabilities = new ProcessingRunnerCapabilities(
            ProcessingRunnerProtocol.Version,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.RuntimeIdentifier,
            RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            string.IsNullOrWhiteSpace(resourceClass) ? DefaultResourceClass : resourceClass,
            gpuAvailable,
            string.IsNullOrWhiteSpace(latencyClass) ? DefaultLatencyClass : latencyClass,
            (labels ?? []).Distinct(StringComparer.Ordinal).OrderBy(static label => label, StringComparer.Ordinal).ToArray(),
            recipes,
            maxConcurrency,
            maxTransferBytes,
            stages,
            warmup is null
                ? ProcessingRunnerWarmState.Cold
                : stages.IsWarm
                    ? ProcessingRunnerWarmState.Warm
                    : ProcessingRunnerWarmState.Degraded);
        capabilities.Validate();
        return capabilities;
    }

    /// <summary>Validates protocol limits; throws <see cref="ProcessingRunnerProtocolException"/> when violated.</summary>
    public void Validate()
    {
        if (ProtocolVersion != ProcessingRunnerProtocol.Version)
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.InvalidCapabilities, "The runner protocol version is unsupported.");
        }
        if (string.IsNullOrWhiteSpace(OperatingSystem) || string.IsNullOrWhiteSpace(OsArchitecture)
            || string.IsNullOrWhiteSpace(ProcessArchitecture) || string.IsNullOrWhiteSpace(RuntimeIdentifier)
            || string.IsNullOrWhiteSpace(FrameworkDescription))
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.InvalidCapabilities, "Runner platform identity is incomplete.");
        }
        if (ProcessorCount < 1 || TotalMemoryBytes < 0)
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.InvalidCapabilities, "Runner resource counts are invalid.");
        }
        if (!ProcessingRunnerProtocol.IsValidLabel(ResourceClass) || !ProcessingRunnerProtocol.IsValidLabel(LatencyClass))
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.InvalidCapabilities, "Runner resource and latency classes must be labels.");
        }
        if (Labels is null || Labels.Count > ProcessingRunnerProtocol.MaximumLabelCount
            || Labels.Any(static label => !ProcessingRunnerProtocol.IsValidLabel(label))
            || Labels.Distinct(StringComparer.Ordinal).Count() != Labels.Count)
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.InvalidCapabilities, "Runner labels are invalid or duplicated.");
        }
        if (BuiltInRecipes is null || BuiltInRecipes.Count == 0
            || BuiltInRecipes.Count > ProcessingRunnerProtocol.MaximumRecipeCount
            || BuiltInRecipes.Any(static recipe => string.IsNullOrWhiteSpace(recipe.Name)
                || string.IsNullOrWhiteSpace(recipe.SemanticVersion)
                || string.IsNullOrWhiteSpace(recipe.ImplementationVersion))
            || BuiltInRecipes.Select(static recipe => recipe.Name).Distinct(StringComparer.Ordinal).Count() != BuiltInRecipes.Count)
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.InvalidCapabilities, "Runner recipe capabilities are invalid.");
        }
        if (MaxConcurrency < 1 || MaxConcurrency > ProcessingRunnerProtocol.MaximumConcurrency)
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.InvalidCapabilities,
                $"Runner concurrency must be between 1 and {ProcessingRunnerProtocol.MaximumConcurrency}.");
        }
        if (MaxTransferBytes < 1 || MaxTransferBytes > ProcessingRunnerProtocol.MaximumTransferBytes)
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.InvalidCapabilities, "Runner transfer limit is out of range.");
        }
        ArgumentNullException.ThrowIfNull(Warmup);
    }

    /// <summary>
    /// Returns the advertised recipes whose declared versions match the host's built-in definitions. A runner that
    /// advertises a different implementation of a recipe is never matched to that recipe.
    /// </summary>
    public IReadOnlyList<string> ResolveVersionMatchedRecipes()
        => BuiltInRecipes
            .Where(static recipe => BuiltInProcessingRecipes.TryGetDefinition(recipe.Name, out var definition)
                && definition is not null
                && string.Equals(definition.SemanticVersion, recipe.SemanticVersion, StringComparison.Ordinal)
                && string.Equals(definition.ImplementationVersion, recipe.ImplementationVersion, StringComparison.Ordinal))
            .Select(static recipe => recipe.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
}

/// <summary>Per-recipe placement requirements the host declares; every declared axis must match a runner label.</summary>
public sealed record ProcessingRunnerJobRequirement(
    string RecipeName,
    string? ResourceClass = null,
    string? LatencyClass = null,
    bool RequiresGpu = false,
    string? ProcessArchitecture = null,
    IReadOnlyList<string>? Labels = null)
{
    public bool IsSatisfiedBy(ProcessingRunnerCapabilities capabilities, IReadOnlyCollection<string> versionMatchedRecipes)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(versionMatchedRecipes);
        return versionMatchedRecipes.Contains(RecipeName, StringComparer.Ordinal)
            && (ResourceClass is null || string.Equals(ResourceClass, capabilities.ResourceClass, StringComparison.Ordinal))
            && (LatencyClass is null || string.Equals(LatencyClass, capabilities.LatencyClass, StringComparison.Ordinal))
            && (!RequiresGpu || capabilities.GpuAvailable)
            && (ProcessArchitecture is null
                || string.Equals(ProcessArchitecture, capabilities.ProcessArchitecture, StringComparison.OrdinalIgnoreCase))
            && (Labels is null || Labels.All(label => capabilities.Labels.Contains(label, StringComparer.Ordinal)));
    }
}

public sealed class ProcessingRunnerProtocolException : Exception
{
    public ProcessingRunnerProtocolException()
    {
    }

    public ProcessingRunnerProtocolException(string message) : base(message)
    {
    }

    public ProcessingRunnerProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public ProcessingRunnerProtocolException(string reasonCode, string message) : base(message)
    {
        ReasonCode = reasonCode;
    }

    public ProcessingRunnerProtocolException(string reasonCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; } = ProcessingRunnerReasonCodes.InvalidCompletion;
}

public static class ProcessingRunnerProcessInfo
{
    public static DateTimeOffset GetProcessStartedUtc()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }
}
