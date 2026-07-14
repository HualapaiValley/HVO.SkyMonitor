using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;

internal sealed class UploadCaptureLaneHandler(
    IArtifactOutbox outbox,
    IOptions<CameraAgentHostOptions> options) : ICaptureLaneHandler
{
    private readonly IArtifactOutbox _outbox = outbox;
    private readonly string _root = Path.GetFullPath(options.Value.RawIngressRoot);

    public string Lane => "upload";

    public async ValueTask<CaptureLaneHandlerResult> HandleAsync(
        CaptureLaneHandlerContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var descriptor = context.RawCapture.Manifest.Descriptor;
            await _outbox.EnqueueAsync(
                _root,
                new ArtifactUploadManifest(
                    ArtifactUploadManifest.CurrentSchemaVersion,
                    descriptor.Capture.AgentId,
                    descriptor.Artifact.ArtifactId,
                    descriptor.Capture.CaptureId,
                    FrameArtifactRole.Raw,
                    descriptor.Artifact.MediaType,
                    descriptor.Layout.ByteLength,
                    descriptor.Artifact.ChecksumSha256,
                    descriptor.Timing.ExposureStartedUtc,
                    "raw-v1",
                    context.RawCapture.StoredFrame.RelativePath,
                    context.RawCapture.Manifest.Scene),
                cancellationToken).ConfigureAwait(false);
            return CaptureLaneHandlerResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return CaptureLaneHandlerResult.Retry("outbox-io");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return CaptureLaneHandlerResult.Terminal("outbox-invalid");
        }
    }
}
