namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed record CameraAgentArchiveCardLinks(
    Guid? ExecutionId,
    string RunUnavailableReason,
    IReadOnlyList<Guid> CandidateIds,
    bool CandidateLinksAvailable);

internal interface ICameraAgentArchiveCardUiService
{
    ValueTask<CameraAgentArchiveCardLinks> GetLinksAsync(Guid captureId, CancellationToken cancellationToken);
}

/// <summary>Resolves this capture's links through the existing authorized, durable read boundaries.</summary>
internal sealed class CameraAgentArchiveCardUiService(
    ICameraAgentProcessingGraphUiService processing,
    ICameraAgentTransientUiService transients) : ICameraAgentArchiveCardUiService
{
    public async ValueTask<CameraAgentArchiveCardLinks> GetLinksAsync(Guid captureId, CancellationToken cancellationToken)
    {
        var run = await processing.GetLiveExecutionIdAsync(captureId, cancellationToken).ConfigureAwait(false);
        var stages = await transients.GetCaptureStagesAsync(captureId, cancellationToken).ConfigureAwait(false);
        var stagesAvailable = stages.IsSuccess && stages.Value?.CaptureId == captureId;
        return new(
            run.IsSuccess && run.Value?.ExecutionId is { } id && id != Guid.Empty ? id : null,
            run.Kind == OperatorUiResultKind.NotFound
                ? "No live pipeline run was recorded for this capture."
                : "The pipeline run identity is unavailable.",
            stagesAvailable
                ? stages.Value!.Events.Where(static stage => stage.CandidateId is { } id && id != Guid.Empty)
                    .Select(static stage => stage.CandidateId!.Value).Distinct().Order().ToArray()
                : [],
            stagesAvailable);
    }
}
