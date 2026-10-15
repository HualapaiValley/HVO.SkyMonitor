using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;

internal sealed class UploadCaptureLaneHandler(
    IArtifactOutbox outbox,
    IOptions<CameraAgentHostOptions> options,
    CaptureProcessingPersistence? persistence = null) : ICaptureLaneHandler
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
            if (context.RawCapture.Manifest.Scene?.RequiresProjectedScene == true)
            {
                if (persistence is null)
                    return CaptureLaneHandlerResult.Terminal("outbox-scene-persistence-unavailable");
                var gate = StorageLifecycleLock.ForRoot(_root);
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var scene = await persistence.FindCommittedSceneUploadAsync(
                        context.RawCapture.Manifest.Descriptor, cancellationToken).ConfigureAwait(false);
                    // Upload and standard processing are independent durable lanes. A normal
                    // raw-first arrival must not consume the transient-failure retry budget.
                    if (scene is null)
                    {
                        // Abandoned standard work (a superseded plan or an expired live execution) never commits a
                        // scene. The raw is already enqueued, so the upload finishes without one; the abandoned
                        // standard row records why the scene is unavailable, and archived replay can rebuild it.
                        var abandoned = await persistence.ReadAbandonedStandardWorkReasonAsync(
                            context.RawCapture.Manifest.Descriptor.Capture.CaptureId, cancellationToken).ConfigureAwait(false);
                        return abandoned is null
                            ? CaptureLaneHandlerResult.Wait("outbox-scene-pending")
                            : CaptureLaneHandlerResult.Success;
                    }
                    await _outbox.EnqueueAsync(_root, scene, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }
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
