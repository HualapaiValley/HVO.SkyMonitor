namespace HVO.SkyMonitor.AgentCore;

/// <summary>Consumes a prepared attempt once through one device-owned setting and start path.</summary>
/// <remarks>An exception leaves the attempt uncertain. A caller must retire it before attempting another exposure.</remarks>
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

        await fenceAttachment("instance:" + module.Id, cancellationToken).ConfigureAwait(false);
        await module.InitializeAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    Task<CaptureResult> CaptureBoundAsync(
        ExposureAttemptKey attempt,
        CaptureRequest request,
        CancellationToken cancellationToken);
}
