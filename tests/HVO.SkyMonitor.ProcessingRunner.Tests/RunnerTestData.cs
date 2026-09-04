using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.ProcessingRunner.Contracts;

namespace HVO.SkyMonitor.ProcessingRunner.Tests;

internal static class RunnerTestData
{
    public const string RunnerId = "test-runner-01";

    public static readonly byte[] NoOpPayload = [0];

    public static ProcessingRunnerClientOptions ClientOptions() => new()
    {
        RunnerId = RunnerId,
        ClientId = "system-processing-runner",
        ClientSecret = "secret"
    };

    public static HttpClient CreateHttpClient(ScriptedHttpHandler handler)
        => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://logichost.test/") };

    public static ProcessingRunnerCapabilities Capabilities()
        => ProcessingRunnerCapabilities.CreateForCurrentProcess(1, 1024 * 1024, null, null, null, ProcessingRunnerWarmupStages.NotRun());

    public static ProcessingRunnerRegistrationResponse Registration(
        TimeSpan? heartbeat = null, TimeSpan? claimBackoff = null, IReadOnlyList<string>? eligible = null)
        => new(
            RunnerId,
            Guid.NewGuid(),
            ProcessingRunnerRegistrationStatus.Active,
            heartbeat ?? TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromSeconds(30),
            claimBackoff ?? TimeSpan.FromMilliseconds(50),
            eligible ?? [BuiltInProcessingRecipes.NoOpAnalyzer],
            DateTimeOffset.UtcNow);

    /// <summary>A claim for the no-op analyzer over one layout-less single-byte input (the same shape as warm-up).</summary>
    public static ProcessingRunnerClaim NoOpClaim(Guid? jobId = null, Guid? artifactId = null, TimeSpan? renewalInterval = null)
    {
        var device = Guid.NewGuid();
        var artifact = artifactId ?? Guid.NewGuid();
        var input = new ProcessingRunnerArtifactMetadata(
            artifact,
            device,
            $"/api/v1.0/devices/{device:D}/artifacts/{artifact:D}/content",
            NoOpPayload.Length,
            ProcessingRunnerProtocol.ComputeSha256(NoOpPayload),
            FrameArtifactRole.Raw,
            "runner-warmup",
            new string('0', 64),
            "application/octet-stream",
            null,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            new ProcessingCompatibilityIdentity("w", "w", "w", "w", "w", "w", "w"),
            null, null, null, null, null,
            ProcessingProductKind.PixelData, null, null, null, null);
        return new ProcessingRunnerClaim(
            ProcessingRunnerProtocol.Version,
            jobId ?? Guid.NewGuid(),
            ProcessingRunnerJobClass.CentralRecipe,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(2),
            renewalInterval ?? TimeSpan.FromSeconds(30),
            1,
            3,
            BuiltInProcessingRecipes.NoOpAnalyzer,
            JsonSerializer.SerializeToElement(new { }),
            ProcessingInputSelector.Raw("runner-warmup"),
            [input],
            "runner-warmup",
            null,
            null,
            artifact,
            new string('1', 64),
            null,
            new ProcessingRunnerJobCorrelation(null, null, null, null));
    }
}
