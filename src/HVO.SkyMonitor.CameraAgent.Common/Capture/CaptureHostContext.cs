using System;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

internal sealed class CaptureHostContext(
    CameraModuleConfig configuration,
    IRawCaptureIngress rawCaptureIngress,
    ICaptureDistributor captureDistributor,
    EnvironmentalCaptureTriggerBridge? environmentalTriggers = null,
    IProjectedSceneStagingStore? projectedSceneStaging = null,
    ProjectedSceneStageLifecycleCoordinator? projectedSceneLifecycle = null,
    CaptureProjectedSceneStager? projectedSceneStager = null,
    ILogger? logger = null) : ICaptureHostContext
{
    private static readonly Meter Meter = new("HVO.SkyMonitor.CameraAgent.ProcessingGraph");
    private static readonly Counter<long> ProjectedSceneStagingOutcomes = Meter.CreateCounter<long>(
        "camera_agent.processing.projected_scene_staging.outcomes");
    private readonly CameraModuleConfig _configuration = configuration;
    private readonly IRawCaptureIngress _rawCaptureIngress = rawCaptureIngress;
    private readonly ICaptureDistributor _captureDistributor = captureDistributor;

    public CameraModuleConfig Configuration => _configuration;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Physical projected-scene analysis is optional; acquired raw pixels must reach ingress for any recoverable analysis/provider failure.")]
    public async ValueTask PublishAsync(CaptureLoopSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var stagingFailed = false;
        if (projectedSceneStager is not null)
        {
            try
            {
                submission = await projectedSceneStager.StageAsync(_configuration, submission, cancellationToken)
                    .ConfigureAwait(false);
                ProjectedSceneStagingOutcomes.Add(1, new KeyValuePair<string, object?>("outcome", "staged"));
            }
            catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
            {
                stagingFailed = true;
                var reason = ClassifyStagingFailure(exception);
                submission = MarkProjectedSceneUnavailable(submission, reason);
                ProjectedSceneStagingOutcomes.Add(1, new KeyValuePair<string, object?>("outcome", reason));
                (logger ?? NullLogger.Instance).ProjectedSceneStagingUnavailable(reason);
            }
        }
        RawCaptureReceipt? receipt;
        try
        {
            var ingressCancellation = stagingFailed && cancellationToken.IsCancellationRequested
                ? CancellationToken.None
                : cancellationToken;
            receipt = await _rawCaptureIngress.AcceptAsync(_configuration, submission, ingressCancellation).ConfigureAwait(false);
        }
        catch
        {
            var publicationState = await GetPublicationStateOrUnknownAsync(submission).ConfigureAwait(false);
            if (publicationState == RawCapturePublicationState.DefinitelyNotCommitted)
                await TryDeleteCaptureOwnedStageAsync(submission).ConfigureAwait(false);
            await ResolvePendingStageAsync(submission).ConfigureAwait(false);
            throw;
        }
        await ResolvePendingStageAsync(submission).ConfigureAwait(false);
        if (receipt is null)
        {
            await _captureDistributor.ProcessEphemeralAsync(
                _configuration, submission, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (environmentalTriggers is not null)
        {
            await environmentalTriggers.AfterCaptureAsync(receipt, submission, cancellationToken).ConfigureAwait(false);
        }
        _captureDistributor.NotifyCommittedCapture();
    }

    private static CaptureLoopSubmission MarkProjectedSceneUnavailable(
        CaptureLoopSubmission submission,
        string reason)
    {
        if (submission.Result.Frame is not { } frame) return submission;
        var extra = frame.Metadata.Extra is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(frame.Metadata.Extra, StringComparer.Ordinal);
        extra["projectedSceneAvailability"] = "Unavailable";
        extra["projectedSceneUnavailableReason"] = reason;
        return submission with
        {
            Result = submission.Result with
            {
                Frame = frame with
                {
                    Metadata = frame.Metadata with { Scene = null, Extra = extra }
                }
            }
        };
    }

    private static string ClassifyStagingFailure(Exception exception) => exception switch
    {
        OperationCanceledException => "cancelled",
        IOException => "storage-unavailable",
        UnauthorizedAccessException => "storage-unavailable",
        _ => "analysis-unavailable"
    };

    private ValueTask ResolvePendingStageAsync(CaptureLoopSubmission submission)
        => projectedSceneLifecycle is not null && TryGetStageKey(submission) is { } stageKey
            ? projectedSceneLifecycle.ResolvePendingAsync(stageKey)
            : ValueTask.CompletedTask;

    private static string? TryGetStageKey(CaptureLoopSubmission submission)
        => submission.Result.Frame?.Metadata.Scene is
        {
            ProjectedSceneStageSchemaVersion: StagedProjectedSceneDocument.CurrentSchemaVersion,
            ProjectedSceneStageKey: { Length: 64 } stageKey
        }
            ? stageKey
            : null;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "An unavailable ownership query is ambiguous and must preserve the original publication exception.")]
    private async ValueTask<RawCapturePublicationState> GetPublicationStateOrUnknownAsync(
        CaptureLoopSubmission submission)
    {
        try
        {
            return await _rawCaptureIngress.GetPublicationStateAsync(
                _configuration, submission, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return RawCapturePublicationState.Unknown;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Best-effort cleanup must preserve the original raw publication exception.")]
    private async ValueTask TryDeleteCaptureOwnedStageAsync(CaptureLoopSubmission submission)
    {
        if (projectedSceneStaging is null || TryGetStageKey(submission) is not { } stageKey)
            return;
        try
        {
            await projectedSceneStaging.DeleteAsync(stageKey, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (logger ?? NullLogger.Instance).ProjectedSceneStageCleanupFailed(exception);
        }
    }
}
