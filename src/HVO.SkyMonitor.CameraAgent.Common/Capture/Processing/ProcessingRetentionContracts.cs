namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public sealed record ProcessingRetentionHold(
    Guid ArtifactId,
    string PayloadRelativePath,
    string SidecarRelativePath);

public interface IProcessingRetentionHolds
{
    ValueTask<IReadOnlyList<ProcessingRetentionHold>> GetRetentionHoldsAsync(
        string storageRoot,
        CancellationToken cancellationToken);
}
