using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Upload;

/// <summary>Durably queues stored artifact manifests until central ingestion acknowledges them.</summary>
public interface IArtifactOutbox
{
    ValueTask EnqueueAsync(string root, ArtifactUploadManifest manifest, CancellationToken cancellationToken);
    IReadOnlyList<ArtifactUploadManifest> List(string root, int maximumResults);
    ValueTask AcknowledgeAsync(string root, string idempotencyKey, CancellationToken cancellationToken);
}
