using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Replay;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.ReplayRunner;

internal sealed record RunnerWarmupResult(
    ProcessingRecipeExecutor Executor,
    ReplayRunnerWarmupStages Stages);

internal static class RunnerWarmup
{
    internal static async Task<RunnerWarmupResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var executor = new ProcessingRecipeExecutor();
        var outcome = await executor.ExecuteAsync(CreateWarmupRequest(), cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        var runtime = outcome.Status == ProcessingOutcomeStatus.Produced
            ? new ReplayRunnerWarmupStage(
                "runtime-jit",
                ReplayRunnerWarmupStatus.Completed,
                stopwatch.Elapsed,
                "ProcessingRecipeExecutor and the no-op built-in recipe were executed.")
            : new ReplayRunnerWarmupStage(
                "runtime-jit",
                ReplayRunnerWarmupStatus.Failed,
                stopwatch.Elapsed,
                $"Warmup returned {outcome.Status} ({outcome.ReasonCode ?? "no-reason"}).");
        var nativeStopwatch = Stopwatch.StartNew();
        var nativeOutcome = await executor.ExecuteAsync(CreateNativeWarmupRequest(), cancellationToken).ConfigureAwait(false);
        nativeStopwatch.Stop();
        var native = nativeOutcome.Status == ProcessingOutcomeStatus.Produced
            ? new ReplayRunnerWarmupStage(
                "native-libraries",
                ReplayRunnerWarmupStatus.Completed,
                nativeStopwatch.Elapsed,
                "The JPEG codec and its native imaging library were initialized.")
            : new ReplayRunnerWarmupStage(
                "native-libraries",
                ReplayRunnerWarmupStatus.Failed,
                nativeStopwatch.Elapsed,
                $"Native warmup returned {nativeOutcome.Status} ({nativeOutcome.ReasonCode ?? "no-reason"}).");
        var stages = new ReplayRunnerWarmupStages(
            runtime,
            native,
            new("catalog", ReplayRunnerWarmupStatus.NotApplicable, TimeSpan.Zero, "Catalog access is outside the recipe-kernel boundary."),
            new("calibration", ReplayRunnerWarmupStatus.NotApplicable, TimeSpan.Zero, "Calibration data is supplied as explicit replay artifacts."),
            new("models", ReplayRunnerWarmupStatus.Unsupported, TimeSpan.Zero, "No model runtime is used by built-in recipes."),
            new("gpu", ReplayRunnerWarmupStatus.Unsupported, TimeSpan.Zero, "GPU execution is not supported by this runner."));
        return new RunnerWarmupResult(executor, stages);
    }

    private static ProcessingExecutionRequest CreateWarmupRequest()
    {
        var input = new ProcessingArtifact(
            Guid.NewGuid(),
            FrameArtifactRole.Raw,
            "runner-warmup",
            new string('0', 64),
            "application/octet-stream",
            null,
            new byte[] { 0 },
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            new ProcessingCompatibilityIdentity(
                "runner-warmup",
                "runner-warmup",
                "runner-warmup",
                "runner-warmup",
                "runner-warmup",
                "runner-warmup",
                "runner-warmup"));
        return new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.NoOpAnalyzer,
            JsonSerializer.SerializeToElement(new { }),
            ProcessingInputSelector.Raw("runner-warmup"),
            [input],
            "runner-warmup",
            InputArtifactId: input.ArtifactId);
    }

    private static ProcessingExecutionRequest CreateNativeWarmupRequest()
    {
        var recipeIdentity = new string('0', 64);
        var input = new ProcessingArtifact(
            Guid.NewGuid(),
            FrameArtifactRole.Preview,
            "runner-native-warmup",
            recipeIdentity,
            "application/x-hvo-frame",
            new FrameLayoutDescriptor(
                2,
                2,
                2,
                CameraPixelFormat.Mono8,
                FrameByteOrder.NotApplicable,
                8,
                8,
                FrameSamplePacking.ByteAligned,
                ColorFilterArrayPattern.None,
                null,
                byte.MaxValue,
                4),
            new byte[] { 0, 64, 128, 255 },
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            new ProcessingCompatibilityIdentity(
                "runner-warmup",
                "runner-warmup",
                "runner-warmup",
                "runner-warmup",
                "runner-warmup",
                "runner-warmup",
                "runner-warmup"));
        return new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.JpegEncoding,
            JsonSerializer.SerializeToElement(new JpegEncodingOptions(JpegQuality: 1)),
            ProcessingInputSelector.RecipeResult(
                FrameArtifactRole.Preview,
                input.Variant,
                recipeIdentity),
            [input],
            "runner-native-warmup",
            InputArtifactId: input.ArtifactId);
    }
}
