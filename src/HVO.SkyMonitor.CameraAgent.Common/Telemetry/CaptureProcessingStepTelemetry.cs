using System;

namespace HVO.SkyMonitor.CameraAgent.Common.Telemetry;

public sealed record CaptureProcessingStepTelemetry(
    string Name,
    TimeSpan Duration,
    bool Succeeded,
    string? ErrorMessage);
