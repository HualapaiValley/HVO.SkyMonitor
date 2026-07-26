using System.Diagnostics;
using HVO.SkyMonitor.CameraAgent.Common.Capture;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;

public sealed class CalibrationLibraryOperationsCoordinator(
    SqliteCalibrationLibraryStore store,
    CaptureAdmissionCoordinator admissionCoordinator,
    CalibrationTelemetry? telemetry = null,
    ICalibrationPublicationFaultInjector? faultInjector = null)
{
    private readonly SqliteCalibrationLibraryStore _store = store;
    private readonly CaptureAdmissionCoordinator _admissionCoordinator = admissionCoordinator;
    private readonly CalibrationTelemetry? _telemetry = telemetry;
    private readonly ICalibrationPublicationFaultInjector _faultInjector =
        faultInjector ?? NullCalibrationPublicationFaultInjector.Instance;

    public Task<CalibrationLibraryStateSnapshot> ActivateAsync(
        string bundleId,
        string idempotencyKey,
        long expectedVersion,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            "activate",
            expectedVersion,
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
            "rollback",
            expectedVersion,
            token => _store.RollbackAsync(
                bundleId, idempotencyKey, expectedVersion, actor, reason, token),
            cancellationToken);

    private async Task<CalibrationLibraryStateSnapshot> ExecuteAsync(
        string commandKind,
        long expectedVersion,
        Func<CancellationToken, Task<CalibrationLibraryStateSnapshot>> command,
        CancellationToken cancellationToken)
    {
        using var activity = CalibrationTelemetry.ActivitySource.StartActivity("calibration.activate");
        activity?.SetTag("calibration.command", commandKind);
        try
        {
            var result = await _admissionCoordinator.ExecuteCaptureBoundaryAsync(
                async boundaryToken =>
                {
                    var committed = await command(boundaryToken).ConfigureAwait(false);
                    _faultInjector.Inject(CalibrationPublicationFaultPoint.AfterActivationCommitted, commandKind);
                    return committed;
                },
                cancellationToken).ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _telemetry?.RecordActivation(commandKind, "failure", expectedVersion);
            _telemetry?.RecordFailure("activate", "other");
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }
}
