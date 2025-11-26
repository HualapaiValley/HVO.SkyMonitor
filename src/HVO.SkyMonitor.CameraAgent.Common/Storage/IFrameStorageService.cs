using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Storage;

public interface IFrameStorageService
{
    ValueTask<StoredFrameReference> SaveAsync(CameraModuleConfig config, CameraFrame frame, CancellationToken cancellationToken);
}

public sealed record StoredFrameReference(string RelativePath, string AbsolutePath, DateTimeOffset TimestampUtc);
