using System;

namespace HVO.SkyMonitor.AgentCore;

public sealed record CaptureRequest(
    DateTimeOffset RequestedStartUtc,
    TimeSpan TargetInterval,
    CaptureMode Mode,
    CaptureSetpoint? RequestedSetpoint = null);

public sealed record CaptureResult(
    CameraFrame? Frame,
    CaptureSetpoint NextSetpoint,
    TimeSpan ProcessingLatency,
    CaptureMode Mode,
    bool RequiresImmediateUpload,
    FrameArtifactSet? Artifacts = null);

public sealed record CaptureSetpoint(
    TimeSpan Exposure,
    double Gain,
    TimeSpan? NextIntervalOverride,
    double? TargetFps);

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
