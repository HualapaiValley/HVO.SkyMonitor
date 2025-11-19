namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public sealed record CaptureProcessingStepMetadata(
    string Id,
    string Type,
    int Order);
