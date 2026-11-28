namespace HVO.SkyMonitor.AgentCore;

/// <summary>Consumes a prepared attempt once through one device-owned setting and start path.</summary>
/// <remarks>A failure other than a certified pre-start refusal leaves the attempt uncertain.</remarks>
public interface ICameraBoundExposureCapture
{
    /// <summary>Fences an attachment before the module can write setup controls.</summary>
    async Task InitializeBoundAsync(
        CameraModuleConfig configuration,
        Func<string, CancellationToken, ValueTask> fenceAttachment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fenceAttachment);
        if (this is not ICameraModule module)
            throw new InvalidOperationException("A centered capture transport must also be a camera module.");

        await fenceAttachment("attachment:" + Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
        await module.InitializeAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether the transport enforces the request's exclusive limit at its shutter start.</summary>
    bool EnforcesBoundStartLimit => false;

    Task<CaptureResult> CaptureBoundAsync(
        ExposureAttemptKey attempt,
        CaptureRequest request,
        CancellationToken cancellationToken);
}
