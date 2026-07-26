using HVO.SkyMonitor.CameraAgent.Common.Capture;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;

public sealed class CalibrationLibraryOperationsCoordinator(
    SqliteCalibrationLibraryStore store,
    CaptureAdmissionCoordinator admissionCoordinator)
{
    private readonly SqliteCalibrationLibraryStore _store = store;
    private readonly CaptureAdmissionCoordinator _admissionCoordinator = admissionCoordinator;

    public Task<CalibrationLibraryStateSnapshot> ActivateAsync(
        string bundleId,
        string idempotencyKey,
        long expectedVersion,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            token => _store.ActivateAsync(
                bundleId, idempotencyKey, expectedVersion, actor, reason, token),
            cancellationToken);

    public Task<CalibrationLibraryStateSnapshot> RollbackAsync(
        string bundleId,
        string idempotencyKey,
        long expectedVersion,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            token => _store.RollbackAsync(
                bundleId, idempotencyKey, expectedVersion, actor, reason, token),
            cancellationToken);

    private async Task<CalibrationLibraryStateSnapshot> ExecuteAsync(
        Func<CancellationToken, Task<CalibrationLibraryStateSnapshot>> command,
        CancellationToken cancellationToken)
        => await _admissionCoordinator.ExecuteCaptureBoundaryAsync(
            async boundaryToken => await command(boundaryToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
}
