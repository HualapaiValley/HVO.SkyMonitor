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
        CancellationToken cancellationToken,
        CancellationToken interruptionToken = default);
}

internal interface IAcceptanceRetentionControl : IProcessingRetentionHolds;

internal interface IRawIngressLifecycleProcessingRetentionHolds
{
    ValueTask<IReadOnlyList<ProcessingRetentionHold>> GetRetentionHoldsUnderLifecycleLockAsync(
        string storageRoot,
        CancellationToken cancellationToken);
}

internal sealed class CompositeProcessingRetentionHolds(
    CaptureProcessingPersistence persistence,
    CameraAgentClearReferenceLoader clearReferences,
    SqliteCalibrationLibraryStore calibrationLibrary,
    IAcceptanceRetentionControl? acceptanceControl = null,
    TimeLapses.SqliteTimeLapseStore? timeLapses = null,
    SqliteCalibrationFamilyStore? calibrationFamilies = null) :
    IProcessingRetentionHolds,
    IRawIngressLifecycleProcessingRetentionHolds,
    IProcessingOutputExpiration
{
    public ValueTask<IReadOnlyList<ProcessingRetentionHold>> GetRetentionHoldsAsync(
        string storageRoot,
        CancellationToken cancellationToken)
        => GetRetentionHoldsCoreAsync(storageRoot, initializeRawIngress: true, cancellationToken);

    ValueTask<IReadOnlyList<ProcessingRetentionHold>>
        IRawIngressLifecycleProcessingRetentionHolds.GetRetentionHoldsUnderLifecycleLockAsync(
            string storageRoot,
            CancellationToken cancellationToken)
        => GetRetentionHoldsCoreAsync(storageRoot, initializeRawIngress: false, cancellationToken);

    private async ValueTask<IReadOnlyList<ProcessingRetentionHold>> GetRetentionHoldsCoreAsync(
        string storageRoot,
        bool initializeRawIngress,
        CancellationToken cancellationToken)
    {
        var persisted = await persistence.GetRetentionHoldsAsync(storageRoot, cancellationToken).ConfigureAwait(false);
        var configured = await clearReferences.GetRetentionHoldsAsync(storageRoot, cancellationToken).ConfigureAwait(false);
        var calibration = initializeRawIngress
            ? await calibrationLibrary.GetRetentionHoldsAsync(storageRoot, cancellationToken).ConfigureAwait(false)
            : await calibrationLibrary.GetRetentionHoldsUnderLifecycleLockAsync(storageRoot, cancellationToken).ConfigureAwait(false);
        var acceptance = acceptanceControl is null
            ? []
            : await acceptanceControl.GetRetentionHoldsAsync(storageRoot, cancellationToken).ConfigureAwait(false);
        var videos = timeLapses is null ? [] : await timeLapses.GetRetentionHoldsAsync(storageRoot, cancellationToken).ConfigureAwait(false);
        // The masters of every active calibration family stay held whatever their bundle's own retention_hold, until the
        // family is deactivated. So do the masters a frozen normalization plan names, even once their family is
        // superseded, while its execution is pending or running and then while its node's outputs are kept (#1475).
        // The families store and the processing store are each read under their own gate, never the library's.
        IReadOnlyList<ProcessingRetentionHold> masters = [];
        if (calibrationLibrary.OwnsStorageRoot(storageRoot))
        {
            var familyMasters = calibrationFamilies is null
                ? []
                : await calibrationFamilies.GetActiveMasterArtifactIdsAsync(cancellationToken).ConfigureAwait(false);
            var planMasters = await persistence.GetNormalizationPlanMasterArtifactIdsAsync(storageRoot, cancellationToken)
                .ConfigureAwait(false);
            masters = await calibrationLibrary.GetMasterRetentionHoldsUnderLifecycleLockAsync(
                storageRoot, [.. familyMasters.Union(planMasters)], cancellationToken).ConfigureAwait(false);
        }
        return persisted.Concat(configured).Concat(calibration).Concat(acceptance).Concat(videos).Concat(masters).Distinct().ToArray();
    }

    public ValueTask<int> ExpireOutputsAsync(
        string storageRoot,
        DateTimeOffset committedBeforeUtc,
        IReadOnlySet<string> heldAbsolutePaths,
        CancellationToken cancellationToken,
        CancellationToken interruptionToken = default)
        => persistence.ExpireOutputsAsync(storageRoot, committedBeforeUtc, heldAbsolutePaths, cancellationToken, interruptionToken);
}
