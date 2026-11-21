using System;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
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
    ILogger? logger = null,
    SqliteCalibrationLibraryStore? calibrationLibrary = null,
    string? centeredDeviceIdentity = null,
    CenteredOwnerTransition? centeredOwner = null) : ICaptureHostContext
{
    private static readonly Meter Meter = new("HVO.SkyMonitor.CameraAgent.ProcessingGraph");
    private static readonly Counter<long> ProjectedSceneStagingOutcomes = Meter.CreateCounter<long>(
        "camera_agent.processing.projected_scene_staging.outcomes");
    private readonly CameraModuleConfig _configuration = configuration;
    private readonly IRawCaptureIngress _rawCaptureIngress = rawCaptureIngress;
    private readonly ICaptureDistributor _captureDistributor = captureDistributor;

    public CameraModuleConfig Configuration => _configuration;

    internal RawCaptureIngress CenteredIngress => _rawCaptureIngress as RawCaptureIngress ??
        throw new InvalidOperationException("Centered exposure requires CameraAgent durable raw ingress.");

    internal async Task<TimeSpan> PublishCenteredAsync(
        CaptureLoopSubmission submission,
        CenteredExposureReservation reservation,
        CancellationToken cancellationToken)
    {
        RawCaptureReceipt receipt;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        TimeSpan ingressElapsed;
        try
        {
            receipt = await CenteredIngress.AcceptCenteredAsync(
                _configuration, submission, reservation, cancellationToken).ConfigureAwait(false);
            ingressElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        }
        catch
        {
            if (await GetPublicationStateOrUnknownAsync(submission).ConfigureAwait(false) ==
                RawCapturePublicationState.DefinitelyNotCommitted)
                await TryDeleteCaptureOwnedStageAsync(submission).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await ResolvePendingStageAsync(submission).ConfigureAwait(false);
        }

        if (environmentalTriggers is not null)
            await environmentalTriggers.AfterCaptureAsync(receipt, submission, cancellationToken).ConfigureAwait(false);
        _captureDistributor.NotifyCommittedCapture();
        return ingressElapsed;
    }

    internal Task<CenteredExposureReservation> ReserveCenteredAsync(
        CenteredScheduleBinding? schedule, bool admissionInterrupted, CancellationToken cancellationToken)
        => CenteredIngress.ReserveCenteredAsync(
            _configuration, projectedSceneStager?.FrozenResources ?? new(null, null, null, null, "unavailable"),
            calibrationLibrary, cancellationToken, centeredDeviceIdentity, schedule, admissionInterrupted);

    internal Task DispatchCenteredAsync(
        ExposureAttemptKey attempt, CaptureSetpoint setpoint, CaptureMeteringEvidence? metering, string reason,
        CancellationToken cancellationToken)
        => CenteredIngress.DispatchCenteredAsync(attempt, setpoint, metering, reason, cancellationToken, centeredOwner);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Physical projected-scene analysis is optional; acquired raw pixels must reach ingress for any recoverable analysis/provider failure.")]
    public async ValueTask PublishAsync(CaptureLoopSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var stagingFailed = false;
        RawCaptureGraphBinding? binding = null;
        if (projectedSceneStager is not null)
        {
            var bindingPrepared = false;
            try
            {
                binding = await _rawCaptureIngress.PrepareGraphBindingAsync(
                    _configuration, submission, cancellationToken).ConfigureAwait(false);
                bindingPrepared = true;
                if (binding?.AlreadyPublished != true)
                {
                    var sceneConfiguration = binding is null
                        ? _configuration
                        : _configuration with { Pipeline = binding.Revision.Pipeline };
                    submission = await projectedSceneStager.StageAsync(sceneConfiguration, submission, cancellationToken)
                        .ConfigureAwait(false);
                }
                ProjectedSceneStagingOutcomes.Add(1, new KeyValuePair<string, object?>("outcome", "staged"));
            }
            catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
            {
                stagingFailed = true;
                var reason = ClassifyStagingFailure(exception);
                // Ingress retries a failed binding lookup and still needs the original supplied scene
                // to reject conflicting retry evidence. VirtualSky's captured render is always authoritative.
                if (!string.Equals(_configuration.ModuleType, "VirtualSky", StringComparison.OrdinalIgnoreCase))
                    submission = MarkProjectedSceneUnavailable(submission, reason, preserveSuppliedScene: !bindingPrepared);
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
            receipt = await _rawCaptureIngress.AcceptAsync(_configuration, submission, binding, ingressCancellation).ConfigureAwait(false);
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
        string reason,
        bool preserveSuppliedScene)
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
                    Metadata = frame.Metadata with { Scene = preserveSuppliedScene ? frame.Metadata.Scene : null, Extra = extra }
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
