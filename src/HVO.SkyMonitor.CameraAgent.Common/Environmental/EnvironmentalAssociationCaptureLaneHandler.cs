using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

internal sealed class EnvironmentalAssociationCaptureLaneHandler(IServiceProvider services) : ICaptureLaneHandler
{
    public string Lane => "environment-association";

    public async ValueTask<CaptureLaneHandlerResult> HandleAsync(
        CaptureLaneHandlerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var coordinator = services.GetRequiredService<EnvironmentalAcquisitionCoordinator>();
        var associations = services.GetRequiredService<EnvironmentalAssociationService>();
        var descriptor = context.RawCapture.Manifest.Descriptor;
        var capture = descriptor.Capture;
        var timing = descriptor.Timing;
        var observedAtUtc = timing.ReadoutCompletedUtc.ToUniversalTime();
        var afterCapture = await coordinator.AcquireTriggerAsync(
            EnvironmentalAcquisitionTrigger.AfterCapture,
            observedAtUtc,
            capture.CaptureSequence,
            capture.CaptureId,
            cancellationToken).ConfigureAwait(false);
        var everyNth = await coordinator.AcquireTriggerAsync(
            EnvironmentalAcquisitionTrigger.EveryNthCapture,
            observedAtUtc,
            capture.CaptureSequence,
            capture.CaptureId,
            cancellationToken).ConfigureAwait(false);
        if (afterCapture.Concat(everyNth).Any(static receipt =>
            receipt.Disposition == EnvironmentalAcquisitionDisposition.Coalesced))
        {
            return CaptureLaneHandlerResult.Retry("environment-source-coalesced");
        }
        var exposureFromUtc = timing.ExposureStartedUtc.ToUniversalTime();
        var exposureThroughUtc = timing.ExposureEndedUtc.ToUniversalTime();
        if (exposureThroughUtc <= exposureFromUtc)
        {
            exposureThroughUtc = exposureFromUtc.AddTicks(1);
        }
        _ = await associations.AssociateAsync(
            capture.CaptureId,
            capture.CaptureSequence,
            exposureFromUtc,
            exposureThroughUtc,
            capture.RigId,
            Enum.GetValues<EnvironmentalObservationKind>(),
            cancellationToken).ConfigureAwait(false);
        return CaptureLaneHandlerResult.Success;
    }
}
