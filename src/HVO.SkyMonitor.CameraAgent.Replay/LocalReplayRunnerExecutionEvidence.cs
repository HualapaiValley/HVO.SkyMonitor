namespace HVO.SkyMonitor.CameraAgent.Replay;

public sealed record LocalReplayRunnerExecutionEvidence(
    Guid JobId,
    string RecipeName,
    TimeSpan DispatchDuration,
    TimeSpan ExecutionDuration,
    long RequestMetadataBytes,
    long RequestPayloadBytes,
    int RequestPayloadFrames,
    long ResponseMetadataBytes,
    long ResponsePayloadBytes,
    int ResponsePayloadFrames,
    int HeartbeatCount);
