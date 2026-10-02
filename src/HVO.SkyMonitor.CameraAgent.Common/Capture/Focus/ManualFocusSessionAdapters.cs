namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;

/// <summary>
/// Holds exclusive camera acquisition through the existing capture owner for the duration of one action. The capture
/// admission gate is closed and drained before the action runs and reopened only when it was Running, so normal
/// capture can never interleave with a preview and a failed preview cannot be admitted as a normal capture.
/// </summary>
public sealed class CaptureAdmissionManualFocusExclusiveAcquisition(CaptureAdmissionCoordinator admissionCoordinator)
    : IManualFocusExclusiveAcquisition
{
    private readonly CaptureAdmissionCoordinator _admissionCoordinator = admissionCoordinator;

    public Task<T> ExecuteExclusiveAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _admissionCoordinator.ExecuteCaptureBoundaryAsync(action, cancellationToken);
    }
}

/// <summary>
/// The production default: no live preview frame source exists on this CameraAgent, so a session is unavailable with
/// an explicit reason. It never fabricates a preview.
/// </summary>
public sealed class UnavailableManualFocusPreviewFrameSource(string reason) : IManualFocusPreviewFrameSource
{
    private readonly string _reason = reason;

    public ManualFocusSessionAvailability GetAvailability() => ManualFocusSessionAvailability.Unavailable(_reason);

    public ValueTask<ManualFocusPreviewFrame> AcquireAsync(
        ManualFocusPreviewSettings settings,
        ManualFocusRegion? region,
        CancellationToken cancellationToken)
        => throw new ManualFocusSessionUnavailableException(_reason);
}
