using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Replay;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>Last observed replay runner declaration, or an explicit not-yet-observed state.</summary>
public sealed record ReplayRunnerFactsView(
    string ConfiguredProfile,
    int ConfiguredMaximumConcurrency,
    int ConfiguredDeadlineSeconds,
    bool CapabilitiesObserved,
    ReplayRunnerCapabilityFactsView? Capabilities,
    ReplayRunnerExecutionFactsView? LastExecution);

public sealed record ReplayRunnerCapabilityFactsView(
    int ProtocolVersion,
    string OperatingSystem,
    string OsArchitecture,
    string ProcessArchitecture,
    string RuntimeIdentifier,
    string FrameworkDescription,
    DateTimeOffset ProcessStartedUtc,
    int MaxConcurrency,
    long MaxTransferBytes,
    IReadOnlyList<string> BuiltInRecipes,
    IReadOnlyList<ReplayRunnerWarmupFactView> Warmup);

public sealed record ReplayRunnerWarmupFactView(string Name, string Status, TimeSpan Elapsed, string Detail);

public sealed record ReplayRunnerExecutionFactsView(
    string RecipeName,
    TimeSpan DispatchDuration,
    TimeSpan ExecutionDuration,
    long RequestBytes,
    int RequestPayloadFrames,
    long ResponseBytes,
    int ResponsePayloadFrames,
    int HeartbeatCount);

/// <summary>
/// Read-only projection of the replay runner facts the transport already observed. It never
/// probes, never connects, and never participates in runner lifecycle; runner configuration and
/// lifecycle remain owned by the runner issue. A null client means the local runner profile is
/// not selected or its transport could not be constructed, which renders as "not yet observed".
/// </summary>
public sealed class CameraAgentReplayRunnerFactsProjection(
    IOptions<CameraAgentHostOptions> hostOptions,
    LocalReplayRunnerClient? runner = null)
{
    private readonly IOptions<CameraAgentHostOptions> _hostOptions =
        hostOptions ?? throw new ArgumentNullException(nameof(hostOptions));

    public ReplayRunnerFactsView Create()
    {
        var graphs = _hostOptions.Value.ProcessingGraphs;
        var capabilities = runner?.LastCapabilities;
        var evidence = runner?.LastExecutionEvidence;
        return new ReplayRunnerFactsView(
            graphs.ReplayProfile.ToString(),
            graphs.ReplayMaximumConcurrency,
            graphs.ReplayDeadlineSeconds,
            capabilities is not null,
            capabilities is null ? null : new ReplayRunnerCapabilityFactsView(
                capabilities.ProtocolVersion,
                capabilities.OperatingSystem,
                capabilities.OsArchitecture,
                capabilities.ProcessArchitecture,
                capabilities.RuntimeIdentifier,
                capabilities.FrameworkDescription,
                capabilities.ProcessStartedUtc,
                capabilities.MaxConcurrency,
                capabilities.MaxTransferBytes,
                [.. capabilities.BuiltInRecipes.Select(static recipe =>
                    $"{recipe.Name} {recipe.SemanticVersion}")],
                [.. Warmup(capabilities.Warmup)]),
            evidence is null ? null : new ReplayRunnerExecutionFactsView(
                evidence.RecipeName,
                evidence.DispatchDuration,
                evidence.ExecutionDuration,
                evidence.RequestMetadataBytes + evidence.RequestPayloadBytes,
                evidence.RequestPayloadFrames,
                evidence.ResponseMetadataBytes + evidence.ResponsePayloadBytes,
                evidence.ResponsePayloadFrames,
                evidence.HeartbeatCount));
    }

    private static IEnumerable<ReplayRunnerWarmupFactView> Warmup(ReplayRunnerWarmupStages? stages)
    {
        if (stages is null)
        {
            yield break;
        }
        foreach (var stage in new[]
        {
            stages.RuntimeJit, stages.NativeLibraries, stages.Catalog,
            stages.Calibration, stages.Models, stages.Gpu
        })
        {
            yield return new ReplayRunnerWarmupFactView(
                stage.Name, stage.Status.ToString(), stage.Elapsed, stage.Detail);
        }
    }
}
