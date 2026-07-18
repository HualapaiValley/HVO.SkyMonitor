namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public sealed record CaptureProcessingStepRegistration(
    string Alias,
    Type ImplementationType,
    Type OptionsType,
    int DefaultOrder = 0,
    bool AutoInclude = true);
