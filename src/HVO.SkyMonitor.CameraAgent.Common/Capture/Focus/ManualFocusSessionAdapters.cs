using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;

/// <summary>Takes temporary previews and dispatches motor operations only on the capture owner's pinned generation,
/// after normal captures have drained. The module restores its request-scoped preview settings on every exit.</summary>
public sealed class CameraModuleManualFocusPreviewSource(
    CameraModuleOwnership ownership,
    CaptureAdmissionCoordinator admission,
    TimeProvider timeProvider,
    IMotorizedFocuser? focuser = null) : IManualFocusPreviewSource
{
    internal IMotorizedFocuser? Focuser => focuser;
    internal CaptureAdmissionCoordinator Admission => admission;

    public Task EnsureAdmissionAvailableAsync(CancellationToken cancellationToken)
        => admission.EnsureLifecycleCaptureAllowedAsync(cancellationToken);

    public ManualFocusSessionAvailability GetAvailability()
    {
        if (admission.Snapshot.State == CaptureAdmissionState.Unavailable)
            return ManualFocusSessionAvailability.Unavailable(ManualFocusReasonCodes.AdmissionFailClosed);
        var module = ownership.Snapshot;
        if (!module.Published)
            return ManualFocusSessionAvailability.Unavailable(ManualFocusReasonCodes.NoModule);
        if (module.FocusPreview is not { } fidelity)
            return ManualFocusSessionAvailability.Unavailable(
                ManualFocusReasonCodes.ModuleWithoutPreview(module.ModuleType ?? "unknown"));
        return new(true, fidelity.Limitation, module.ModuleType, fidelity, module.SimulatedFocus, module.Generation);
    }

    public Task<ManualFocusPreview> AcquireAsync(ManualFocusPreviewSettings settings, double? simulatedFocusPosition,
        CancellationToken cancellationToken) => AcquireAsync(settings, simulatedFocusPosition, null, cancellationToken);

    public Task<ManualFocusPreview> AcquireAsync(ManualFocusPreviewSettings settings, double? simulatedFocusPosition,
        long? expectedGeneration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return AtBoundaryAsync(expectedGeneration, (lease, preview, token) =>
            CaptureAsync(lease, preview, settings, simulatedFocusPosition, token), cancellationToken, motorOperation: false);
    }

    internal async Task<ManualFocusPreview> CaptureAsync(CameraModuleLease lease, ICameraFocusPreviewCapture preview,
        ManualFocusPreviewSettings settings, double? simulatedFocusPosition, CancellationToken token)
    {
        var requestedUtc = timeProvider.GetUtcNow();
        var request = new CaptureRequest(requestedUtc, settings.Exposure, CaptureMode.Still,
            new CaptureSetpoint(settings.Exposure, settings.Gain, null, null));
        var result = await preview.CaptureFocusPreviewAsync(new(request, simulatedFocusPosition), token).ConfigureAwait(false);
        var frame = result.Frame ?? throw new InvalidDataException("The camera module returned no preview frame.");
        var imageCircle = lease.Config is { } config ? CameraModuleRunner.ResolveMeteringImageCircle(config, frame) : null;
        return new(frame, lease.Module.ModuleType, lease.Generation, preview.FocusPreviewFidelity, preview.SimulatedFocus,
            requestedUtc, timeProvider.GetUtcNow(), imageCircle);
    }

    internal async Task<T> AtBoundaryAsync<T>(long? expectedGeneration,
        Func<CameraModuleLease, ICameraFocusPreviewCapture, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken, bool motorOperation = true)
    {
        await EnsureAdmissionAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (admission.Snapshot.State == CaptureAdmissionState.Unavailable) throw AdmissionUnavailable();
        if (!ownership.TryAcquire(out var lease))
            throw new ManualFocusSessionUnavailableException(ManualFocusReasonCodes.CameraWithdrawn, ManualFocusReasonCodes.NoModule);
        using (lease)
        {
            if (expectedGeneration is { } expected && lease.Generation != expected)
                throw Withdrawn();
            if (lease.Module is not ICameraFocusPreviewCapture preview)
                throw new ManualFocusSessionUnavailableException(ManualFocusReasonCodes.CameraWithdrawn,
                    ManualFocusReasonCodes.ModuleWithoutPreview(lease.Module.ModuleType));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Revoked);
            try
            {
                async Task<T> Execute(CancellationToken token)
                {
                    token.ThrowIfCancellationRequested();
                    if (admission.Snapshot.State == CaptureAdmissionState.Unavailable) throw AdmissionUnavailable();
                    return await operation(lease, preview, token).ConfigureAwait(false);
                }
                return motorOperation
                    ? await admission.ExecuteMotorBoundaryAsync(Execute, linked.Token).ConfigureAwait(false)
                    : await admission.ExecuteCaptureBoundaryAsync(Execute, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lease.Revoked.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw Withdrawn();
            }
            catch (CaptureAdmissionUnavailableException)
            {
                throw AdmissionUnavailable();
            }
        }
    }

    private static ManualFocusSessionUnavailableException Withdrawn()
        => new(ManualFocusReasonCodes.CameraWithdrawn, ManualFocusReasonCodes.CameraWithdrawnMessage);

    private static ManualFocusSessionUnavailableException AdmissionUnavailable()
        => new(ManualFocusReasonCodes.AdmissionUnavailable, ManualFocusReasonCodes.AdmissionFailClosed);
}
