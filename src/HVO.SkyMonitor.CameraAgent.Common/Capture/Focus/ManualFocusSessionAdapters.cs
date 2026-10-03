using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;

/// <summary>
/// Takes focus previews from the module that <see cref="CameraCaptureService"/> owns, never from a second instance. Each
/// preview holds a <see cref="CameraModuleLease"/> and runs inside the capture admission boundary: the admission gate is
/// closed and in-flight captures drained before the preview, and the gate is reopened only when it was Running, so a
/// preview can never interleave with a normal capture, is never admitted as one, and leaves a paused agent paused.
/// Preview settings are request-scoped; the module contract requires its control state to be unchanged afterwards.
/// </summary>
public sealed class CameraModuleManualFocusPreviewSource(
    CameraModuleOwnership ownership,
    CaptureAdmissionCoordinator admission,
    TimeProvider timeProvider) : IManualFocusPreviewSource
{
    private readonly CameraModuleOwnership _ownership = ownership;
    private readonly CaptureAdmissionCoordinator _admission = admission;
    private readonly TimeProvider _timeProvider = timeProvider;

    public ManualFocusSessionAvailability GetAvailability()
    {
        if (_admission.Snapshot.State == CaptureAdmissionState.Unavailable)
        {
            return ManualFocusSessionAvailability.Unavailable(ManualFocusReasonCodes.AdmissionFailClosed);
        }
        var module = _ownership.Snapshot;
        if (!module.Published)
        {
            return ManualFocusSessionAvailability.Unavailable(ManualFocusReasonCodes.NoModule);
        }
        if (module.FocusPreview is not { } fidelity)
        {
            return ManualFocusSessionAvailability.Unavailable(
                ManualFocusReasonCodes.ModuleWithoutPreview(module.ModuleType ?? "unknown"));
        }
        return new(true, fidelity.Limitation, module.ModuleType, fidelity, module.SimulatedFocus, module.Generation);
    }

    public Task<ManualFocusPreview> AcquireAsync(
        ManualFocusPreviewSettings settings,
        double? simulatedFocusPosition,
        CancellationToken cancellationToken)
        => AcquireAsync(settings, simulatedFocusPosition, null, cancellationToken);

    public async Task<ManualFocusPreview> AcquireAsync(ManualFocusPreviewSettings settings,
        double? simulatedFocusPosition, long? expectedGeneration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_admission.Snapshot.State == CaptureAdmissionState.Unavailable)
        {
            throw AdmissionUnavailable();
        }
        if (!_ownership.TryAcquire(out var lease))
        {
            throw new ManualFocusSessionUnavailableException(ManualFocusReasonCodes.CameraWithdrawn,
                ManualFocusReasonCodes.NoModule);
        }
        using (lease)
        {
            if (expectedGeneration is { } expected && lease.Generation != expected)
            {
                throw new ManualFocusSessionUnavailableException(ManualFocusReasonCodes.CameraWithdrawn,
                    ManualFocusReasonCodes.CameraWithdrawnMessage);
            }
            if (lease.Module is not ICameraFocusPreviewCapture preview)
            {
                throw new ManualFocusSessionUnavailableException(ManualFocusReasonCodes.CameraWithdrawn,
                    ManualFocusReasonCodes.ModuleWithoutPreview(lease.Module.ModuleType));
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Revoked);
            DateTimeOffset requestedUtc = default;
            CaptureResult result;
            try
            {
                result = await _admission.ExecuteCaptureBoundaryAsync(token =>
                {
                    if (_admission.Snapshot.State == CaptureAdmissionState.Unavailable)
                    {
                        throw AdmissionUnavailable();
                    }
                    requestedUtc = _timeProvider.GetUtcNow();
                    var request = new CaptureRequest(
                        requestedUtc,
                        settings.Exposure,
                        CaptureMode.Still,
                        new CaptureSetpoint(settings.Exposure, settings.Gain, null, null));
                    return preview.CaptureFocusPreviewAsync(
                        new CameraFocusPreviewRequest(request, simulatedFocusPosition), token);
                }, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                lease.Revoked.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new ManualFocusSessionUnavailableException(ManualFocusReasonCodes.CameraWithdrawn,
                    ManualFocusReasonCodes.CameraWithdrawnMessage);
            }
            catch (CaptureAdmissionUnavailableException)
            {
                throw AdmissionUnavailable();
            }
            var frame = result.Frame ?? throw new InvalidDataException("The camera module returned no preview frame.");
            // Samples outside the calibrated image circle are not sky; measurement treats them as masked, as metering does.
            var imageCircle = lease.Config is { } config ? CameraModuleRunner.ResolveMeteringImageCircle(config, frame) : null;
            return new ManualFocusPreview(frame, lease.Module.ModuleType, lease.Generation,
                preview.FocusPreviewFidelity, preview.SimulatedFocus, requestedUtc, _timeProvider.GetUtcNow(), imageCircle);
        }
    }

    private static ManualFocusSessionUnavailableException AdmissionUnavailable()
        => new(ManualFocusReasonCodes.AdmissionUnavailable, ManualFocusReasonCodes.AdmissionFailClosed);
}
