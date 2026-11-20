namespace HVO.SkyMonitor.AgentCore;

/// <summary>Stable local camera identity within an agent.</summary>
public sealed record CameraKey(string AgentId, string CameraId);

/// <summary>Identifies an admitted capture within its frozen acquisition run.</summary>
public sealed record ExposureKey(CameraKey Camera, Guid RunId, Guid CaptureId);

/// <summary>Identifies a reserved acquisition before its canonical capture GUID is known.</summary>
public sealed record ExposureAttemptKey(CameraKey Camera, Guid RunId, long Ordinal, Guid AttemptId);
