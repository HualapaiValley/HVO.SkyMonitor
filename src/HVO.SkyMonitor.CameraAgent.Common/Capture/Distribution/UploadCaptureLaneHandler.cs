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
            await _outbox.EnqueueAsync(
                _root,
                context.RawCapture.Manifest,
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
        catch (ArtifactOutboxConflictException)
        {
            return CaptureLaneHandlerResult.Terminal("outbox-conflict");
        }
    }
}
