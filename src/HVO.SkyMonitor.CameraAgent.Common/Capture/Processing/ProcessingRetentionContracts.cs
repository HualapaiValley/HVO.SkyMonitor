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

internal sealed class CompositeProcessingRetentionHolds(
    CaptureProcessingPersistence persistence,
    CameraAgentClearReferenceLoader clearReferences) : IProcessingRetentionHolds
{
    public async ValueTask<IReadOnlyList<ProcessingRetentionHold>> GetRetentionHoldsAsync(
        string storageRoot,
        CancellationToken cancellationToken)
    {
        var persisted = await persistence.GetRetentionHoldsAsync(storageRoot, cancellationToken).ConfigureAwait(false);
        var configured = await clearReferences.GetRetentionHoldsAsync(storageRoot, cancellationToken).ConfigureAwait(false);
        return persisted.Concat(configured).Distinct().ToArray();
    }
}
