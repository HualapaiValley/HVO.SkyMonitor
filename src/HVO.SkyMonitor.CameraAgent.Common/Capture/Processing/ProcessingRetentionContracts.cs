using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;

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

internal interface IProcessingOutputExpiration
{
    ValueTask<int> ExpireOutputsAsync(
        string storageRoot,
        DateTimeOffset committedBeforeUtc,
        IReadOnlySet<string> heldAbsolutePaths,
        CancellationToken cancellationToken);
}

internal interface IAcceptanceRetentionControl : IProcessingRetentionHolds;

internal sealed class CompositeProcessingRetentionHolds(
    CaptureProcessingPersistence persistence,
    CameraAgentClearReferenceLoader clearReferences,
    SqliteCalibrationLibraryStore calibrationLibrary,
    IAcceptanceRetentionControl? acceptanceControl = null) : IProcessingRetentionHolds, IProcessingOutputExpiration
{
    public async ValueTask<IReadOnlyList<ProcessingRetentionHold>> GetRetentionHoldsAsync(
        string storageRoot,
        CancellationToken cancellationToken)
    {
        var persisted = await persistence.GetRetentionHoldsAsync(storageRoot, cancellationToken).ConfigureAwait(false);
        var configured = await clearReferences.GetRetentionHoldsAsync(storageRoot, cancellationToken).ConfigureAwait(false);
        var calibration = await calibrationLibrary.GetRetentionHoldsAsync(storageRoot, cancellationToken).ConfigureAwait(false);
        var acceptance = acceptanceControl is null
            ? []
            : await acceptanceControl.GetRetentionHoldsAsync(storageRoot, cancellationToken).ConfigureAwait(false);
        return persisted.Concat(configured).Concat(calibration).Concat(acceptance).Distinct().ToArray();
    }

    public ValueTask<int> ExpireOutputsAsync(
        string storageRoot,
        DateTimeOffset committedBeforeUtc,
        IReadOnlySet<string> heldAbsolutePaths,
        CancellationToken cancellationToken)
        => persistence.ExpireOutputsAsync(storageRoot, committedBeforeUtc, heldAbsolutePaths, cancellationToken);
}
