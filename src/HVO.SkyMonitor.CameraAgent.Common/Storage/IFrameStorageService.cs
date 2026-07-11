using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Storage;

public interface IFrameStorageService
{
    ValueTask<StoredFrameReference> SaveAsync(CameraModuleConfig config, FrameArtifact artifact, CancellationToken cancellationToken);

    IReadOnlyList<StoredFrameReference> List(string storageRoot, DateOnly utcDate, FrameArtifactRole? role, int maximumResults);
}

public sealed record StoredFrameReference(string RelativePath, string AbsolutePath, DateTimeOffset TimestampUtc, FrameArtifactRole Role);
