using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public sealed class EnvironmentalCaptureTriggerBridge(
    EnvironmentalAcquisitionCoordinator coordinator,
    EnvironmentalAcquisitionService service,
    IEnvironmentalAcquisitionStateStore stateStore,
    IOptions<CameraAgentHostOptions> options)
{
    public async ValueTask BeforeCaptureAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!options.Value.EnvironmentalAcquisition.Enabled)
        {
            return;
        }
        var request = new EnvironmentalTriggerRequest(EnvironmentalAcquisitionTrigger.BeforeCapture, observedAtUtc);
        var budget = options.Value.EnvironmentalAcquisition.BeforeCaptureWaitMilliseconds;
        if (budget == 0)
        {
            _ = service.TryEnqueue(request);
            return;
        }
        _ = await coordinator.AcquireTriggerWithBudgetAsync(request.Trigger, request.ObservedAtUtc, budget,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AfterCaptureAsync(
        RawCaptureReceipt receipt,
        CaptureLoopSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(submission);
        if (!options.Value.EnvironmentalAcquisition.Enabled)
        {
            return;
        }
        var capture = receipt.Manifest.Descriptor.Capture;
        var observedAtUtc = submission.Result.AcquisitionTiming?.ReadoutCompletedUtc.ToUniversalTime()
            ?? submission.Result.Frame?.TimestampUtc.ToUniversalTime()
            ?? submission.CaptureStartedUtc.ToUniversalTime();
        var regime = submission.CycleEvidence?.SolarRegime;
        if (regime.HasValue && await stateStore.RecordCaptureRegimeAsync(
            options.Value.RawIngressRoot,
            capture.CaptureSequence,
            capture.CaptureId,
            regime.Value,
            observedAtUtc,
            cancellationToken).ConfigureAwait(false))
        {
            _ = service.TryEnqueue(new EnvironmentalTriggerRequest(
                EnvironmentalAcquisitionTrigger.RegimeChange,
                observedAtUtc,
                capture.CaptureSequence,
                capture.CaptureId));
        }
    }
}
