using System;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.AgentCore;

public sealed record CaptureRequest(
    DateTimeOffset RequestedStartUtc,
    TimeSpan TargetInterval,
    CaptureMode Mode,
    CaptureSetpoint? RequestedSetpoint = null)
{
    [JsonIgnore]
    public string? BoundGraphRevisionId { get; init; }

    [JsonIgnore]
    public CapturePipelineConfig? BoundGraph { get; init; }

    /// <summary>The earliest authorized shutter start for a confirmed bound capture grant.</summary>
    [JsonIgnore]
    public DateTimeOffset? BoundStartNotBeforeUtc { get; init; }

    /// <summary>The exclusive shutter-start limit for a confirmed bound capture grant.</summary>
    [JsonIgnore]
    public DateTimeOffset? BoundStartBeforeUtc { get; init; }

    /// <summary>Check the confirmed grant at the device-owned shutter boundary.</summary>
    public void RequireOpenBoundStart(DateTimeOffset utc) => RequireOpenBoundStart(utc, null);

    /// <summary>Check the shutter sample and its chronology after device controls were applied.</summary>
    public void RequireOpenBoundStart(DateTimeOffset utc, DateTimeOffset? appliedUtc)
    {
        if (BoundStartBeforeUtc is { } limit && utc >= limit ||
            BoundStartNotBeforeUtc is { } opening && (utc < opening || utc < appliedUtc))
            throw new CaptureStartWindowClosedException();
    }
}

public sealed record CaptureResult(
    CameraFrame? Frame,
    CaptureSetpoint NextSetpoint,
    TimeSpan ProcessingLatency,
    CaptureMode Mode,
    bool RequiresImmediateUpload,
    FrameArtifactSet? Artifacts = null)
{
    /// <summary>Module-reported command/status observation boundaries, not exact sensor timing.</summary>
    public CaptureAcquisitionTiming? AcquisitionTiming { get; init; }
}

public sealed record CaptureAcquisitionTiming(
    DateTimeOffset ExposureStartedUtc,
    DateTimeOffset ExposureEndedUtc,
    DateTimeOffset ReadoutCompletedUtc)
{
    /// <summary>Gets when a decided setpoint was applied by the module, when reported.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? SetpointAppliedUtc { get; init; }
}

public sealed record CaptureSetpoint(
    TimeSpan Exposure,
    double Gain,
    TimeSpan? NextIntervalOverride,
    double? TargetFps)
{
    public bool IsValid() =>
        Exposure > TimeSpan.Zero && double.IsFinite(Gain) && Gain >= 0 &&
        (TargetFps is not { } fps || double.IsFinite(fps) && fps > 0) &&
        NextIntervalOverride is not { Ticks: <= 0 };
}

[Flags]
public enum CameraModuleCapabilities
{
    None = 0,
    StillFrames = 1,
    Video = 2,
    AdaptiveGain = 4,
    AdaptiveExposure = 8,
    TemperatureControl = 16,
    AutoGainControl = 32,
    AutoExposureControl = 64
}

public enum CaptureMode
{
    Still,
    Video
}
