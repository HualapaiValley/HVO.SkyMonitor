using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.CameraAgent.Common.Options;

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
        var acquisition = services.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value.EnvironmentalAcquisition;
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
        var policyKinds = ResolvePolicyKinds(acquisition);
        if (policyKinds.Length == 0) return CaptureLaneHandlerResult.Success;
        _ = await associations.AssociateAsync(
            capture.CaptureId,
            capture.CaptureSequence,
            exposureFromUtc,
            exposureThroughUtc,
            capture.RigId,
            policyKinds,
            cancellationToken).ConfigureAwait(false);
        return CaptureLaneHandlerResult.Success;
    }

    internal static EnvironmentalObservationKind[] ResolvePolicyKinds(EnvironmentalAcquisitionOptions acquisition)
    {
        ArgumentNullException.ThrowIfNull(acquisition);
        return acquisition.Enabled
            ? acquisition.Sources.Select(static source => source.Kind).Distinct().Order().ToArray()
            : [];
    }
}
