using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Storage;

public interface IFrameStorageService
{
    ValueTask<StoredFrameReference> SaveAsync(string storageRoot, FrameArtifact artifact, CancellationToken cancellationToken);

    ValueTask RemoveAsync(string storageRoot, StoredFrameReference storedFrame, Guid artifactId, CancellationToken cancellationToken);

    IReadOnlyList<StoredFrameReference> List(string storageRoot, DateOnly utcDate, FrameArtifactRole? role, int maximumResults);
}

public sealed record StoredFrameReference(string RelativePath, string AbsolutePath, DateTimeOffset TimestampUtc, FrameArtifactRole Role);
