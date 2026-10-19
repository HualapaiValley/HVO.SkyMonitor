using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

/// <summary>
/// Whether a capture's standard processing can still produce its overlays and, while it can, the newest earlier
/// capture whose standard processing has finished. A capture without standard work is not pending.
/// </summary>
public sealed record CameraAgentProcessingCompletion(bool Pending, Guid? NewestFinishedCaptureId);

/// <summary>Reads standard processing completion from the durable lane state, without starting any work.</summary>
public interface ICameraAgentProcessingCompletion
{
    ValueTask<CameraAgentProcessingCompletion> ReadAsync(Guid captureId, CancellationToken cancellationToken);
}

internal sealed class CameraAgentProcessingCompletionReader(
    SqliteCaptureProcessingStore store) : ICameraAgentProcessingCompletion
{
    public ValueTask<CameraAgentProcessingCompletion> ReadAsync(Guid captureId, CancellationToken cancellationToken)
        => store.ReadStandardCompletionAsync(captureId, cancellationToken);
}
