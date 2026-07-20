using HVO.SkyMonitor.CameraAgent.Common.Transients;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;

internal sealed class TransientCaptureLaneHandler(ITransientCandidateJournal journal) : ICaptureLaneHandler
{
    private readonly ITransientCandidateJournal _journal = journal;

    public string Lane => "transient";

    public async ValueTask<CaptureLaneHandlerResult> HandleAsync(
        CaptureLaneHandlerContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await _journal.StageCaptureAsync(context, cancellationToken).ConfigureAwait(false);
            return CaptureLaneHandlerResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or FileNotFoundException or TransientCandidateIdentityConflictException)
        {
            return CaptureLaneHandlerResult.Terminal("transient-evidence-invalid");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return CaptureLaneHandlerResult.Retry("transient-journal-io");
        }
    }
}
