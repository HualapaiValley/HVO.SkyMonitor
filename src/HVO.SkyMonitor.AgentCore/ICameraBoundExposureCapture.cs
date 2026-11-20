namespace HVO.SkyMonitor.AgentCore;

/// <summary>Consumes a prepared attempt once through one device-owned setting and start path.</summary>
/// <remarks>An exception leaves the attempt uncertain. A caller must retire it before attempting another exposure.</remarks>
public interface ICameraBoundExposureCapture
{
    Task<CaptureResult> CaptureBoundAsync(
        ExposureAttemptKey attempt,
        CaptureRequest request,
        CancellationToken cancellationToken);
}
