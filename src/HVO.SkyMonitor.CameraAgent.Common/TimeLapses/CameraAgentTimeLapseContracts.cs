using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Video.FFmpeg;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeLapses;

public enum CameraAgentTimeLapseState { Queued, Working, Produced, NoSources, Unavailable, Failed }

/// <summary>Frozen producer settings retained beside the automation occurrence. Later host settings cannot change a retry.</summary>
public sealed record CameraAgentTimeLapsePreset(
    string IdentitySha256,
    string AgentId,
    ObservatoryLocation? LegacyObservatory,
    CameraRigConfig Rig,
    TimeLapseFrameOptions Frames,
    TimeLapseTimingOptions Timing,
    int MaximumDimension);

/// <summary>One durable request for a retained hourly or daily source period.</summary>
public sealed record CameraAgentTimeLapseJob(
    Guid JobId,
    LocalAutomationOccurrence Occurrence,
    LocalAutomationSourceWindow Window,
    Guid? ParentJobId,
    CameraAgentTimeLapsePreset Preset,
    CameraAgentTimeLapseState State,
    DateTimeOffset UpdatedUtc,
    string? ReasonCode,
    Guid? ProductId)
{
    public long Revision { get; init; } = 1;
    public IReadOnlyDictionary<string, int> Exclusions { get; init; } = new Dictionary<string, int>();
}

internal sealed record CameraAgentTimeLapseSourcePlan(
    IReadOnlyList<CameraAgentTimeLapseRawSource> Sources,
    IReadOnlyDictionary<string, int> Exclusions);

internal sealed record CameraAgentTimeLapseRawSource(ReconstructionDescriptor Descriptor, string PayloadRelativePath, string SidecarRelativePath);

/// <summary>Bounded immediate source lineage for one rendered image, including explicit one/two-frame warmup.</summary>
public sealed record CameraAgentTimeLapseFrameLineage(
    Guid ArtifactId,
    string RenderingIdentitySha256,
    IReadOnlyList<Guid> StackSourceIds,
    IReadOnlyList<string> StackPayloadSha256,
    TimeSpan TotalIntegration);

/// <summary>An immutable local-only video. It never becomes an upload artifact or outbox item.</summary>
public sealed record CameraAgentTimeLapseProduct(
    Guid ProductId,
    string OutputIdentitySha256,
    Guid JobId,
    DateOnly ReportDate,
    string ReportingPeriodIdentitySha256,
    string WindowIdentitySha256,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool IsDaily,
    string PresetIdentitySha256,
    TimeLapseTimeline? Timeline,
    IReadOnlyList<CameraAgentTimeLapseFrameLineage> Frames,
    IReadOnlyList<Guid> SegmentProductIds,
    IReadOnlyDictionary<string, int> Exclusions,
    TimeLapseEncodingEvidence Encoding,
    DateTimeOffset CreatedUtc)
{
    public bool HasGaps { get; init; }
    public bool IsGapFiller { get; init; }
}

public sealed record CameraAgentTimeLapseDay(
    DateOnly ReportDate,
    IReadOnlyList<CameraAgentTimeLapseJob> Jobs,
    IReadOnlyList<CameraAgentTimeLapseProduct> Products);

public sealed record CameraAgentTimeLapseDateSummary(DateOnly ReportDate, string ReportingPeriodIdentitySha256,
    CameraAgentTimeLapseState? DailyState, int HourlyProduced, bool HasPending);

/// <summary>Thin library projection; browsing dates never loads full packet/source provenance.</summary>
public sealed record CameraAgentTimeLapseSummary(Guid ProductId, DateOnly ReportDate, int Width, int Height, bool HasGaps);

public interface ICameraAgentTimeLapseCatalog
{
    ValueTask<CameraAgentTimeLapseDay> GetDayAsync(DateOnly reportDate, CancellationToken cancellationToken);
    ValueTask<CameraAgentTimeLapseProduct?> GetAsync(Guid productId, CancellationToken cancellationToken);
    ValueTask<LocalAutomationSourceWindow?> GetWindowAsync(Guid productId, CancellationToken cancellationToken);
    ValueTask<Stream?> OpenVideoAsync(Guid productId, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<CameraAgentTimeLapseSummary>> ListDailyAsync(DateOnly? before, int limit, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<CameraAgentTimeLapseDateSummary>> SummarizeAsync(DateOnly firstDate, DateOnly lastDate, CancellationToken cancellationToken);
}

public interface ICameraAgentTimeLapseCommands
{
    ValueTask<bool> RetryAsync(Guid jobId, long expectedRevision, Guid requestId, string actor, string reason,
        CancellationToken cancellationToken);
}
