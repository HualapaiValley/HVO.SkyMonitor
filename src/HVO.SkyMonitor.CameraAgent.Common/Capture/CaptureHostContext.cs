using System;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

internal sealed class CaptureHostContext(
    CameraModuleConfig configuration,
    IRawCaptureIngress rawCaptureIngress,
    ICaptureDistributor captureDistributor,
    EnvironmentalCaptureTriggerBridge? environmentalTriggers = null,
    IProjectedSceneStagingStore? projectedSceneStaging = null,
    ProjectedSceneStageLifecycleCoordinator? projectedSceneLifecycle = null,
    ILogger? logger = null) : ICaptureHostContext
{
    private readonly CameraModuleConfig _configuration = configuration;
    private readonly IRawCaptureIngress _rawCaptureIngress = rawCaptureIngress;
    private readonly ICaptureDistributor _captureDistributor = captureDistributor;

    public CameraModuleConfig Configuration => _configuration;

    public async ValueTask PublishAsync(CaptureLoopSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        RawCaptureReceipt? receipt;
        try
        {
            receipt = await _rawCaptureIngress.AcceptAsync(_configuration, submission, cancellationToken).ConfigureAwait(false);
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
