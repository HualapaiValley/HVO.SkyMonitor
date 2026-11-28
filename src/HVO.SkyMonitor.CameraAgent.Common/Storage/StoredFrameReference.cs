using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Storage;

internal sealed record StoredFrameReference(
    string RelativePath,
    string AbsolutePath,
    DateTimeOffset TimestampUtc,
    FrameArtifactRole Role);
