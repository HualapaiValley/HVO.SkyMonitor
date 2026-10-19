using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

/// <summary>
/// Whether a capture's standard processing can still produce its overlays and, while it can, the newest earlier
/// captures whose standard processing has ended, newest first. A capture without standard work is not pending.
/// </summary>
public sealed record CameraAgentProcessingCompletion(bool Pending, IReadOnlyList<Guid> NewestFinishedCaptureIds);

/// <summary>Reads standard processing completion from the durable lane state, without starting any work.</summary>
public interface ICameraAgentProcessingCompletion
{
    /// <param name="maximumFinished">How many ended earlier captures to name while the capture is pending.</param>
    ValueTask<CameraAgentProcessingCompletion> ReadAsync(
        Guid captureId,
        int maximumFinished,
        CancellationToken cancellationToken);

    /// <summary>Up to <paramref name="maximum"/> ended captures before <paramref name="captureId"/>, newest first.</summary>
    ValueTask<IReadOnlyList<Guid>> ReadFinishedBeforeAsync(
        Guid captureId,
        int maximum,
        CancellationToken cancellationToken);
}

internal sealed class CameraAgentProcessingCompletionReader(
    SqliteCaptureProcessingStore store) : ICameraAgentProcessingCompletion
{
    public ValueTask<CameraAgentProcessingCompletion> ReadAsync(
        Guid captureId,
        int maximumFinished,
        CancellationToken cancellationToken)
        => store.ReadStandardCompletionAsync(captureId, maximumFinished, cancellationToken);

    public ValueTask<IReadOnlyList<Guid>> ReadFinishedBeforeAsync(
        Guid captureId,
        int maximum,
        CancellationToken cancellationToken)
        => store.ReadFinishedStandardBeforeAsync(captureId, maximum, cancellationToken);
}
