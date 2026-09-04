using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.ProcessingRunner.Contracts;

namespace HVO.SkyMonitor.ProcessingRunner;

internal sealed record RunnerWarmupResult(ProcessingRecipeExecutor Executor, ProcessingRunnerWarmupStages Stages);

/// <summary>
/// Measures the runtime/JIT and native-codec initialization separately so registration advertises real startup
/// cost. The stage set mirrors the CameraAgent local replay runner.
/// </summary>
internal static class RunnerWarmup
{
    internal static async Task<RunnerWarmupResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var executor = new ProcessingRecipeExecutor();
        var outcome = await executor.ExecuteAsync(CreateRequest(BuiltInProcessingRecipes.NoOpAnalyzer, "runner-warmup"), cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();
        var runtime = outcome.Status == ProcessingOutcomeStatus.Produced
            ? new ProcessingRunnerWarmupStage("runtime-jit", ProcessingRunnerWarmupStatus.Completed, stopwatch.Elapsed,
                "ProcessingRecipeExecutor and the no-op built-in recipe were executed.")
            : new ProcessingRunnerWarmupStage("runtime-jit", ProcessingRunnerWarmupStatus.Failed, stopwatch.Elapsed,
                $"Warmup returned {outcome.Status} ({outcome.ReasonCode ?? "no-reason"}).");
        var nativeStopwatch = Stopwatch.StartNew();
        var nativeOutcome = await executor.ExecuteAsync(CreateNativeRequest(), cancellationToken).ConfigureAwait(false);
        nativeStopwatch.Stop();
        var native = nativeOutcome.Status == ProcessingOutcomeStatus.Produced
            ? new ProcessingRunnerWarmupStage("native-libraries", ProcessingRunnerWarmupStatus.Completed, nativeStopwatch.Elapsed,
                "The JPEG codec and its native imaging library were initialized.")
            : new ProcessingRunnerWarmupStage("native-libraries", ProcessingRunnerWarmupStatus.Failed, nativeStopwatch.Elapsed,
                $"Native warmup returned {nativeOutcome.Status} ({nativeOutcome.ReasonCode ?? "no-reason"}).");
        var stages = new ProcessingRunnerWarmupStages(
            runtime,
            native,
            new("catalog", ProcessingRunnerWarmupStatus.NotApplicable, TimeSpan.Zero, "Catalog access is outside the recipe-kernel boundary."),
            new("calibration", ProcessingRunnerWarmupStatus.NotApplicable, TimeSpan.Zero, "Calibration inputs are supplied as artifacts."),
            new("models", ProcessingRunnerWarmupStatus.Unsupported, TimeSpan.Zero, "No model runtime is used by built-in recipes."),
            new("gpu", ProcessingRunnerWarmupStatus.Unsupported, TimeSpan.Zero, "GPU execution is not supported by this runner."));
        return new RunnerWarmupResult(executor, stages);
    }

    private static ProcessingExecutionRequest CreateRequest(string recipeName, string variant)
    {
        var input = CreateArtifact(FrameArtifactRole.Raw, variant, layout: null, new byte[] { 0 });
        return new ProcessingExecutionRequest(
            recipeName,
            JsonSerializer.SerializeToElement(new { }),
            ProcessingInputSelector.Raw(variant),
            [input],
            variant,
            InputArtifactId: input.ArtifactId);
    }

    private static ProcessingExecutionRequest CreateNativeRequest()
    {
        var input = CreateArtifact(
            FrameArtifactRole.Preview,
            "runner-native-warmup",
            new FrameLayoutDescriptor(
                2, 2, 2, CameraPixelFormat.Mono8, FrameByteOrder.NotApplicable, 8, 8,
                FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, null, byte.MaxValue, 4),
            new byte[] { 0, 64, 128, 255 });
        return new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.JpegEncoding,
            JsonSerializer.SerializeToElement(new JpegEncodingOptions(JpegQuality: 1)),
            ProcessingInputSelector.RecipeResult(FrameArtifactRole.Preview, input.Variant, input.RecipeIdentitySha256),
            [input],
            "runner-native-warmup",
            InputArtifactId: input.ArtifactId);
    }

    private static ProcessingArtifact CreateArtifact(
        FrameArtifactRole role,
        string variant,
        FrameLayoutDescriptor? layout,
        byte[] payload)
        => new(
            Guid.NewGuid(),
            role,
            variant,
            new string('0', 64),
            layout is null ? "application/octet-stream" : "application/x-hvo-frame",
            layout,
            payload,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            new ProcessingCompatibilityIdentity(
                "runner-warmup", "runner-warmup", "runner-warmup", "runner-warmup", "runner-warmup", "runner-warmup", "runner-warmup"));
}
